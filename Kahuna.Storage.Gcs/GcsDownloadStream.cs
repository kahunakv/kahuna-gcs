using System.IO.Pipelines;
using Google;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// A readable stream over a Google Cloud Storage object.
/// <para>
/// The download API pushes bytes into a stream the caller supplies; the artifact contract hands a
/// readable stream back. Bridging the two by buffering the object would mean holding a checkpoint file
/// in memory to hash it, so the transfer runs as a background task writing into a pipe and the caller
/// reads the other end — bounded memory, and the download applies backpressure when the reader is
/// slower than the link.
/// </para>
/// <para>
/// A failure part-way surfaces on the read that reaches it rather than being swallowed as an early end
/// of stream. That distinction is what stops a truncated download from being hashed as if it were the
/// whole artifact.
/// </para>
/// </summary>
internal sealed class GcsDownloadStream : Stream
{
    private readonly Pipe _pipe;
    private readonly Stream _reader;
    private readonly Task _downloadTask;
    private readonly CancellationTokenSource _cts;

    private GcsDownloadStream(Pipe pipe, Task downloadTask, CancellationTokenSource cts)
    {
        _pipe = pipe;
        _reader = pipe.Reader.AsStream();
        _downloadTask = downloadTask;
        _cts = cts;
    }

    /// <summary>
    /// Starts the transfer and returns the readable end. A missing object is reported here, before any
    /// bytes are read, so a caller sees "this artifact does not exist" rather than an empty stream.
    /// </summary>
    internal static async Task<Stream> OpenAsync(
        StorageClient client,
        string bucket,
        string key,
        DownloadObjectOptions? options,
        Guid? backupId,
        string relativePath,
        CancellationToken ct)
    {
        // Confirm existence up front. Without it a missing object would surface as a zero-byte read,
        // which every caller here would treat as a valid empty artifact.
        try
        {
            await client.GetObjectAsync(bucket, key, options: null, ct).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
        {
            throw new BackupArtifactException(
                backupId is { } id
                    ? $"Backup {id:N}: artifact '{relativePath}' not found."
                    : $"Backup artifact '{relativePath}' not found.",
                ex);
        }

        Pipe pipe = new();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task download = RunDownloadAsync(client, bucket, key, options, pipe, cts.Token);

        return new GcsDownloadStream(pipe, download, cts);
    }

    private static async Task RunDownloadAsync(
        StorageClient client,
        string bucket,
        string key,
        DownloadObjectOptions? options,
        Pipe pipe,
        CancellationToken ct)
    {
        Stream destination = pipe.Writer.AsStream();

        try
        {
            await client.DownloadObjectAsync(bucket, key, destination, options, ct).ConfigureAwait(false);
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Completing the writer with the error makes the reader throw it at the point the stream
            // runs dry, instead of returning a clean end-of-stream on a partial object.
            await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _reader.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _reader.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _reader.ReadAsync(buffer, offset, count, ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask DisposeAsync()
    {
        // A caller that stops reading early — a ranged read satisfied, or an aborted verification —
        // must not leave a transfer running against a pipe nobody drains.
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);

        try
        {
            await _downloadTask.ConfigureAwait(false);
        }
        catch
        {
            // The transfer was torn down on purpose; whatever it failed with is not the caller's problem.
        }

        _cts.Dispose();
    }
}
