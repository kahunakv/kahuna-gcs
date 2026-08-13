using System.Text.Json;
using Google;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Stores backup manifests as JSON objects in a Google Cloud Storage bucket, at
/// <c>{prefix}{backupId:N}.manifest</c>.
/// <para>
/// Manifests are small and are read whole, so this is ordinary object CRUD — the interesting part is
/// what it refuses to do. A listing failure is never converted into an empty result, because
/// <see cref="ListManifestIdsAsync"/> is the authority on which backups exist and an empty answer
/// tells the orphan sweep that every artifact in the bucket is unowned. And a manifest is published
/// with a create-if-absent precondition by default, so an id collision fails loudly rather than
/// overwriting the record of a backup that already exists.
/// </para>
/// </summary>
public sealed class GcsBackupStorageTarget : IBackupStorageTarget
{
    private const string ManifestContentType = "application/json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly GcsObjectLayout _layout;
    private readonly bool _failOnIdCollision;

    internal GcsBackupStorageTarget(
        StorageClient client, string bucket, GcsObjectLayout layout, bool failOnIdCollision)
    {
        _client = client;
        _bucket = bucket;
        _layout = layout;
        _failOnIdCollision = failOnIdCollision;
    }

    /// <inheritdoc />
    public async Task PutAsync(BackupManifest manifest, CancellationToken ct = default)
    {
        string key = _layout.ManifestKey(manifest.BackupId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        // A generation precondition of 0 means "only if this object does not exist yet", which is a
        // stronger publish primitive than a filesystem rename can offer. Backup ids are freshly
        // generated per backup, so a collision is a symptom — two writers on one prefix, or a bucket
        // restored over a live one — and the existing manifest is the thing least worth destroying.
        UploadObjectOptions? options = _failOnIdCollision ? new UploadObjectOptions { IfGenerationMatch = 0 } : null;

        using MemoryStream source = new(payload, writable: false);

        try
        {
            await _client.UploadObjectAsync(_bucket, key, ManifestContentType, source, options, ct)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (_failOnIdCollision && GcsErrors.IsPreconditionFailed(ex))
        {
            throw new BackupArtifactException(
                $"Backup {manifest.BackupId:N}: a manifest already exists in the bucket. Refusing to overwrite it — " +
                "a repeated backup id means two writers share this prefix, or a bucket has been restored over a " +
                "live one.", ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid backupId, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, _layout.ManifestKey(backupId), options: null, ct)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
        {
            // Already gone. Deleting a manifest tombstones the backup, and callers re-run the delete to
            // converge after a partial failure, so an absent manifest is success, not an error.
        }
    }

    /// <inheritdoc />
    public async Task<BackupManifest?> GetAsync(Guid backupId, CancellationToken ct = default)
    {
        byte[]? bytes = await TryDownloadAsync(_layout.ManifestKey(backupId), ct).ConfigureAwait(false);
        if (bytes is null)
            return null;

        return JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupManifest>> ListAsync(CancellationToken ct = default)
    {
        List<BackupManifest> results = [];

        foreach (string key in await ListManifestKeysAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            byte[]? bytes = await TryDownloadAsync(key, ct).ConfigureAwait(false);
            if (bytes is null)
                continue; // Deleted between the listing and the read; it is simply no longer a backup.

            try
            {
                BackupManifest? manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions);
                if (manifest is not null)
                    results.Add(manifest);
            }
            catch (JsonException)
            {
                // Not dropped silently: ListCorruptAsync reports it, so one unreadable object cannot
                // blind the whole listing and cannot pass as "this backup does not exist" either.
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid backupId, string reason)>> ListCorruptAsync(CancellationToken ct = default)
    {
        List<(Guid, string)> corrupt = [];

        foreach (string key in await ListManifestKeysAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            _layout.TryParseManifestKey(key, out Guid backupId);

            byte[]? bytes;
            try
            {
                bytes = await TryDownloadAsync(key, ct).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                corrupt.Add((backupId, $"Manifest could not be read: {ex.Message}"));
                continue;
            }

            if (bytes is null)
                continue;

            try
            {
                if (JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions) is null)
                    corrupt.Add((backupId, "Manifest deserialized to null."));
            }
            catch (JsonException ex)
            {
                corrupt.Add((backupId, $"Manifest is not valid JSON: {ex.Message}"));
            }
        }

        return corrupt;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListManifestIdsAsync(CancellationToken ct = default)
    {
        List<Guid> ids = [];

        // Names only — no object is read. A manifest that is corrupt, half-written, or transiently
        // unreadable still yields its id here, which is what keeps its artifacts protected from the
        // orphan sweep. Listing failures propagate for the same reason.
        foreach (string key in await ListManifestKeysAsync(ct).ConfigureAwait(false))
        {
            if (_layout.TryParseManifestKey(key, out Guid backupId))
                ids.Add(backupId);
        }

        return ids;
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keys requested per listing response. Null uses the service default, which is what a deployment
    /// runs with; a small value makes a prefix span many pages so the paging path can be exercised
    /// without writing thousands of objects to reach the default boundary.
    /// </summary>
    internal int? ListPageSize { get; set; }

    /// <summary>
    /// Every manifest key under the configured prefix, paged to the end.
    /// <para>
    /// Manifests and artifacts share the prefix, so this filters by name rather than by container. The
    /// listing is deliberately not delimited: a delimiter would return artifact prefixes as synthetic
    /// directory entries and manifests as objects, which is more responses for the same answer.
    /// </para>
    /// </summary>
    private async Task<List<string>> ListManifestKeysAsync(CancellationToken ct)
    {
        List<string> keys = [];

        // Ask only for the fields that are used. A manifest listing needs names alone, and the page
        // token to keep going; requesting the full object metadata makes every page larger for nothing.
        ListObjectsOptions options = new() { Fields = "items(name),nextPageToken", PageSize = ListPageSize };

        await foreach (Google.Apis.Storage.v1.Data.Object item in
                       _client.ListObjectsAsync(_bucket, _layout.RootPrefix, options)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            if (_layout.TryParseManifestKey(item.Name, out _))
                keys.Add(item.Name);
        }

        return keys;
    }

    /// <summary>Downloads an object whole, or returns null when it does not exist.</summary>
    private async Task<byte[]?> TryDownloadAsync(string key, CancellationToken ct)
    {
        using MemoryStream buffer = new();

        try
        {
            await _client.DownloadObjectAsync(_bucket, key, buffer, options: null, ct).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
        {
            return null;
        }

        return buffer.ToArray();
    }
}
