using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using Google;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Stores backup artifact bytes — checkpoint trees and per-partition WAL segments — as objects in a
/// Google Cloud Storage bucket, under <c>{prefix}{backupId:N}/</c>.
/// <para>
/// Three properties of a bucket shape everything here, and none of them are filesystem properties.
/// There is no rename, so publication is a completed upload rather than a temp-then-move. There is no
/// recursive prefix delete, so reclaiming a backup is a listing plus per-object deletes that can fail
/// part-way — every delete path is therefore written to converge when re-run rather than to succeed
/// once. And a backend can neither checkpoint into a bucket nor open one, so a full backup transits a
/// local scratch directory on the way out and a restore materialises back onto local disk on the way
/// in.
/// </para>
/// <para>
/// The POSIX hardening the local target enforces has no analogue here and is not faked: this store
/// declares it unsupported, and the equivalent protections — uniform bucket-level access, public
/// access prevention, encryption at rest — are verified against the bucket at startup instead.
/// </para>
/// </summary>
public sealed class GcsBackupArtifactStore : IBackupArtifactStore
{
    private const int TransferBufferSize = 1 << 20; // 1 MiB streamed chunks — bounded memory per object

    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly GcsObjectLayout _layout;
    private readonly string _scratchRoot;
    private readonly int _uploadThresholdBytes;
    private readonly int _uploadChunkSizeBytes;
    private readonly ILogger _logger;

    internal GcsBackupArtifactStore(
        StorageClient client,
        string bucket,
        GcsObjectLayout layout,
        string scratchRoot,
        int uploadThresholdBytes,
        int uploadChunkSizeBytes,
        ILogger logger)
    {
        _client = client;
        _bucket = bucket;
        _layout = layout;
        _scratchRoot = scratchRoot;
        _uploadThresholdBytes = uploadThresholdBytes;
        _uploadChunkSizeBytes = uploadChunkSizeBytes;
        _logger = logger;
    }

    /// <inheritdoc />
    public BackupArtifactStoreCapabilities Capabilities { get; } = new(
        // Object ACLs are not a substitute for file modes, and pretending otherwise would let a caller
        // believe a security control was applied that never was. The bucket-level equivalents are
        // checked at startup instead.
        SupportsPosixHardening: false,
        // An HTTP range request bills and transfers only the requested bytes.
        SupportsCheapRangeReads: true,
        // No backend can checkpoint into a bucket or open one, so every checkpoint transits local disk.
        RequiresLocalScratch: true);

    /// <inheritdoc />
    public async Task<bool> BackupExistsAsync(Guid backupId, CancellationToken ct = default)
    {
        // One key is enough to answer this; asking for a full page of names would transfer the whole
        // prefix listing to compute a boolean.
        ListObjectsOptions options = new() { PageSize = 1, Fields = "items(name),nextPageToken" };

        await foreach (Google.Apis.Storage.v1.Data.Object _ in
                       _client.ListObjectsAsync(_bucket, _layout.ArtifactPrefix(backupId), options)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid backupId, string relativePath, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectAsync(_bucket, _layout.ArtifactKey(backupId, relativePath), options: null, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupArtifactEntry>> ListAsync(Guid backupId, CancellationToken ct = default)
    {
        string prefix = _layout.ArtifactPrefix(backupId);
        List<BackupArtifactEntry> entries = [];

        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       ListObjectsAsync(prefix, "items(name,size),nextPageToken", ct).ConfigureAwait(false))
        {
            string relative = GcsObjectLayout.RelativeTo(prefix, item.Name);
            if (relative.Length == 0)
                continue;

            entries.Add(new BackupArtifactEntry(relative, (long)(item.Size ?? 0)));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return entries;
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(
        Guid backupId, string relativePath, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        string key = _layout.ArtifactKey(backupId, relativePath);

        DownloadObjectOptions? options = null;
        if (offset > 0 || length is not null)
        {
            // An HTTP range is inclusive of both ends, and an open-ended one (no upper bound) is what
            // "read from here to the end" has to become.
            long? last = length is null ? null : offset + length.Value - 1;

            options = new DownloadObjectOptions
            {
                Range = new RangeHeaderValue(offset, last),
                // The recorded hash covers the whole object, so comparing it against a slice fails on
                // perfectly correct data. The client's automatic mode is supposed to notice that and
                // stand down, but it decides from the response headers — and a server that returns the
                // whole-object hash alongside a partial response defeats it, which is not hypothetical.
                // Turning it off for ranged reads is therefore the deterministic choice, and it costs
                // nothing that matters: a slice can only be checked against a digest of that slice,
                // which nothing records, and artifact integrity is established against the manifest at
                // the point of use. A full read leaves these options null and is still validated.
                DownloadValidationMode = DownloadValidationMode.Never
            };
        }

        // The download API writes into a stream rather than handing one back, so the bytes are pumped
        // through a pipe: the caller reads at its own pace and the transfer applies backpressure
        // instead of materialising the object in memory. Verification hashes multi-gigabyte checkpoint
        // files through exactly this path.
        return await GcsDownloadStream.OpenAsync(_client, _bucket, key, options, backupId, relativePath, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IBackupArtifactWriter> OpenWriteAsync(
        Guid backupId, string relativePath, CancellationToken ct = default) =>
        Task.FromResult<IBackupArtifactWriter>(new GcsArtifactWriter(
            _client,
            _bucket,
            _layout.ArtifactKey(backupId, relativePath),
            _uploadThresholdBytes,
            _uploadChunkSizeBytes));

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListBackupIdsAsync(CancellationToken ct = default)
    {
        HashSet<Guid> ids = [];

        // Deliberately unguarded: a listing failure propagates. This set is the orphan sweep's candidate
        // list, and a transient outage reported as "no artifacts" would be read as "nothing is here",
        // which is the one answer that can never be recovered from.
        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       ListObjectsAsync(_layout.RootPrefix, "items(name),nextPageToken", ct).ConfigureAwait(false))
        {
            if (_layout.TryParseArtifactKey(item.Name, out Guid id))
                ids.Add(id);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Reports no debris, because this store cannot produce any: an upload becomes an object only when
    /// it completes, so an interrupted one leaves nothing addressable behind. A resumable upload that
    /// is abandoned mid-flight leaves an unfinished session, which is not an object, is not listed, and
    /// expires on its own after a week — reclaiming it is not something a client can do, and there is
    /// no key to hand back here that would mean anything.
    /// </summary>
    public Task<IReadOnlyList<BackupArtifactLeftover>> ListLeftoversAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupArtifactLeftover>>([]);

    /// <inheritdoc />
    public Task DeleteLeftoverAsync(BackupArtifactLeftover leftover, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public async Task DeleteAllAsync(Guid backupId, CancellationToken ct = default)
    {
        string prefix = _layout.ArtifactPrefix(backupId);

        // The listing is drained before any delete rather than deleted while enumerating: mutating a
        // prefix mid-listing is what makes page boundaries skip keys, and a skipped key is an artifact
        // that survives a delete that reported success.
        List<string> keys = [];
        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       ListObjectsAsync(prefix, "items(name),nextPageToken", ct).ConfigureAwait(false))
        {
            keys.Add(item.Name);
        }

        foreach (string key in keys)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await _client.DeleteObjectAsync(_bucket, key, options: null, ct).ConfigureAwait(false);
            }
            catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
            {
                // Already deleted by an earlier pass that failed later on. This is the normal shape of a
                // re-run, not an error: the operation converges rather than succeeding exactly once.
            }
        }
    }

    /// <inheritdoc />
    public Task<IBackupCheckpointStagingArea> BeginCheckpointAsync(Guid backupId, CancellationToken ct = default)
    {
        // The parent is created and the leaf deliberately is not: a backend publishes its checkpoint by
        // renaming a sibling temp tree onto this path, and that rename fails if anything already exists
        // there.
        string parent = Path.Combine(_scratchRoot, backupId.ToString("N"));
        Directory.CreateDirectory(parent);

        string localPath = Path.Combine(parent, GcsObjectLayout.CheckpointPrefix);

        return Task.FromResult<IBackupCheckpointStagingArea>(new GcsCheckpointStagingArea(
            this, backupId, localPath, parent));
    }

    /// <inheritdoc />
    public async Task MaterializeAsync(
        Guid backupId,
        string relativePrefix,
        string destinationDirectory,
        long throttleBytesPerSec = 0,
        CancellationToken ct = default)
    {
        string prefix = _layout.ArtifactPrefix(backupId, relativePrefix);

        List<string> keys = [];
        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       ListObjectsAsync(prefix, "items(name),nextPageToken", ct).ConfigureAwait(false))
        {
            keys.Add(item.Name);
        }

        if (keys.Count == 0)
            throw new BackupArtifactException(
                $"Backup {backupId:N}: artifacts under '{relativePrefix}' are missing.");

        Directory.CreateDirectory(destinationDirectory);

        foreach (string key in keys)
        {
            ct.ThrowIfCancellationRequested();

            string relative = GcsObjectLayout.RelativeTo(prefix, key);
            if (relative.Length == 0)
                continue;

            // Relative paths are preserved exactly. A backend opens this tree and is layout-sensitive,
            // so flattening keys or dropping a level produces a directory that looks plausible and
            // cannot be opened.
            string destination = Path.Combine(
                destinationDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            string? parent = Path.GetDirectoryName(destination);
            if (parent is not null)
                Directory.CreateDirectory(parent);

            await DownloadToFileAsync(key, destination, throttleBytesPerSec, ct).ConfigureAwait(false);
        }
    }

    // ── internals used by the staging area ────────────────────────────────────────────────────

    /// <summary>
    /// Uploads one local file to an artifact key. Used to publish a staged checkpoint, where the file is
    /// already on disk and seekable, so whether it streams in chunks is decided from its length up front
    /// rather than discovered part-way through.
    /// </summary>
    internal async Task UploadFileAsync(Guid backupId, string relativePath, string localPath, CancellationToken ct)
    {
        string key = _layout.ArtifactKey(backupId, relativePath);

        await using FileStream source = new(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferSize, useAsync: true);

        UploadObjectOptions? options = source.Length > _uploadThresholdBytes
            ? new UploadObjectOptions { ChunkSize = _uploadChunkSizeBytes }
            : null;

        await _client.UploadObjectAsync(_bucket, key, GcsContentTypes.Artifact, source, options, ct)
            .ConfigureAwait(false);
    }

    internal ILogger Logger => _logger;

    /// <summary>
    /// Keys requested per listing response. Null uses the service default, which is what a deployment
    /// runs with; a small value makes a prefix span many pages so the paging path can be exercised
    /// without writing thousands of objects to reach the default boundary.
    /// </summary>
    internal int? ListPageSize { get; set; }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates every object under a prefix, following page tokens to the end.
    /// <para>
    /// Paging is the whole point: a single response covers a bounded number of keys, and a checkpoint
    /// tree routinely exceeds it. A caller that read one response would silently see a short listing —
    /// which reads as a smaller backup, a partially verified one, or a prefix that has already been
    /// deleted, depending on who asked.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<Google.Apis.Storage.v1.Data.Object> ListObjectsAsync(
        string prefix,
        string fields,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ListObjectsOptions options = new() { Fields = fields, PageSize = ListPageSize };

        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       _client.ListObjectsAsync(_bucket, prefix, options).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Downloads one object onto local disk, pacing the transfer when a throttle is set so a large
    /// restore does not saturate the link and starve foreground traffic.
    /// </summary>
    private async Task DownloadToFileAsync(
        string key, string destination, long throttleBytesPerSec, CancellationToken ct)
    {
        await using FileStream file = new(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferSize, useAsync: true);

        if (throttleBytesPerSec <= 0)
        {
            await _client.DownloadObjectAsync(_bucket, key, file, options: null, ct).ConfigureAwait(false);
            return;
        }

        await using Stream source = await GcsDownloadStream
            .OpenAsync(_client, _bucket, key, options: null, backupId: null, relativePath: key, ct)
            .ConfigureAwait(false);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            long copied = 0;
            long start = Stopwatch.GetTimestamp();
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory(0, TransferBufferSize), ct)
                       .ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;

                double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                double target = (double)copied / throttleBytesPerSec;
                if (target - elapsed > 0.001)
                    await Task.Delay(TimeSpan.FromSeconds(target - elapsed), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
