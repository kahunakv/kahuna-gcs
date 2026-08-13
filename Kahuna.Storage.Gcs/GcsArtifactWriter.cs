using System.IO.Pipelines;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// An artifact upload that publishes only on completion.
/// <para>
/// The contract requires that abandoning a write publishes nothing, and Google Cloud Storage gives
/// that for free: an upload creates no object until its final chunk is committed, so there is no
/// temp-object-then-rename dance and no partial-object debris class. An abandoned session simply
/// expires server-side, leaving nothing that was ever listed or addressable.
/// </para>
/// <para>
/// What the threshold decides is memory, not protocol — every upload is resumable either way. It has
/// to be decided from the bytes actually written rather than declared up front, because the caller
/// streams WAL records through here without knowing the total length in advance. A small artifact
/// buffers and is handed over whole, so the client knows its length and sends one chunk; the moment a
/// write would push past the threshold the buffered prefix is fed to a streaming upload and everything
/// after it follows, so a multi-gigabyte artifact is never resident.
/// </para>
/// </summary>
internal sealed class GcsArtifactWriter : IBackupArtifactWriter
{
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _key;
    private readonly int _thresholdBytes;
    private readonly int _chunkSizeBytes;

    private readonly SpillingUploadStream _stream;

    private bool _completed;
    private bool _disposed;

    internal GcsArtifactWriter(StorageClient client, string bucket, string key, int thresholdBytes, int chunkSizeBytes)
    {
        _client = client;
        _bucket = bucket;
        _key = key;
        _thresholdBytes = thresholdBytes;
        _chunkSizeBytes = chunkSizeBytes;
        _stream = new SpillingUploadStream(this);
    }

    public Stream Stream => _stream;

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("This artifact write has already been completed.");

        await _stream.PublishAsync(ct).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_completed)
        {
            await _stream.DisposeAsyncCore(abandon: false).ConfigureAwait(false);
            return;
        }

        // Abandoned: tear the upload down rather than finishing it. Nothing was ever visible, so there
        // is nothing to clean up in the bucket.
        await _stream.DisposeAsyncCore(abandon: true).ConfigureAwait(false);
    }

    /// <summary>
    /// The write-side stream. Buffers until the threshold is crossed, then switches to a resumable
    /// upload fed through a pipe.
    /// </summary>
    private sealed class SpillingUploadStream : Stream
    {
        private readonly GcsArtifactWriter _owner;
        private readonly MemoryStream _buffer = new();

        private Pipe? _pipe;
        private Stream? _pipeWriteStream;
        private Task? _uploadTask;
        private CancellationTokenSource? _uploadCts;

        private long _written;

        internal SpillingUploadStream(GcsArtifactWriter owner) => _owner = owner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() => _pipeWriteStream?.Flush();

        public override Task FlushAsync(CancellationToken ct) =>
            _pipeWriteStream?.FlushAsync(ct) ?? Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        // Synchronous writes are not a legacy path here: the hashing stream the WAL writer wraps this in
        // flushes its final block synchronously, so this has to work rather than throw.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_pipeWriteStream is null && _written + buffer.Length <= _owner._thresholdBytes)
            {
                _buffer.Write(buffer);
                _written += buffer.Length;
                return;
            }

            byte[] copy = buffer.ToArray();
            WriteAsync(copy.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_pipeWriteStream is null)
            {
                if (_written + buffer.Length <= _owner._thresholdBytes)
                {
                    await _buffer.WriteAsync(buffer, ct).ConfigureAwait(false);
                    _written += buffer.Length;
                    return;
                }

                await SpillAsync(ct).ConfigureAwait(false);
            }

            await _pipeWriteStream!.WriteAsync(buffer, ct).ConfigureAwait(false);
            _written += buffer.Length;
        }

        /// <summary>
        /// Starts the resumable upload and replays everything buffered so far into it, so the object
        /// still receives the bytes written before the threshold was crossed.
        /// </summary>
        private async Task SpillAsync(CancellationToken ct)
        {
            _uploadCts = new CancellationTokenSource();
            _pipe = new Pipe();
            _pipeWriteStream = _pipe.Writer.AsStream();
            _uploadTask = RunUploadAsync(_pipe, _uploadCts.Token);

            if (_buffer.Length > 0)
            {
                await _pipeWriteStream.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), ct)
                    .ConfigureAwait(false);
                _buffer.SetLength(0);
            }
        }

        /// <summary>
        /// Drives the upload, and on failure completes the read side with the error so a writer blocked
        /// on backpressure is released with that exception instead of waiting for a reader that has
        /// already given up.
        /// </summary>
        private async Task RunUploadAsync(Pipe pipe, CancellationToken ct)
        {
            UploadObjectOptions options = new() { ChunkSize = _owner._chunkSizeBytes };

            try
            {
                await _owner._client.UploadObjectAsync(
                    _owner._bucket, _owner._key, GcsContentTypes.Artifact, pipe.Reader.AsStream(), options, ct)
                    .ConfigureAwait(false);

                await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Publishes the artifact: the buffered bytes when it stayed small, or the tail of the stream.</summary>
        internal async Task PublishAsync(CancellationToken ct)
        {
            if (_pipeWriteStream is null)
            {
                _buffer.Position = 0;
                await _owner._client.UploadObjectAsync(
                    _owner._bucket, _owner._key, GcsContentTypes.Artifact, _buffer, options: null, ct)
                    .ConfigureAwait(false);
                return;
            }

            await _pipeWriteStream.FlushAsync(ct).ConfigureAwait(false);
            await _pipe!.Writer.CompleteAsync().ConfigureAwait(false);
            await _uploadTask!.ConfigureAwait(false);
        }

        internal async ValueTask DisposeAsyncCore(bool abandon)
        {
            if (abandon && _pipe is not null)
            {
                // Signal the uploader to stop and release it from the pipe, then wait for it to unwind so
                // the request is not still in flight when the caller moves on. The resumable session is
                // left unfinished, which is exactly how it never becomes an object.
                await _uploadCts!.CancelAsync().ConfigureAwait(false);
                await _pipe.Writer.CompleteAsync(new OperationCanceledException("Artifact upload abandoned."))
                    .ConfigureAwait(false);

                try
                {
                    await _uploadTask!.ConfigureAwait(false);
                }
                catch
                {
                    // The upload was torn down deliberately; its failure is the intended outcome.
                }
            }

            _uploadCts?.Dispose();
            await _buffer.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            // Lifetime belongs to the writer: disposing this stream must not tear down an upload the
            // caller is about to complete. The WAL writer wraps this stream in a hashing stream and
            // disposes that wrapper before calling CompleteAsync.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class GcsContentTypes
{
    /// <summary>
    /// Artifacts are opaque bytes — checkpoint files and WAL segments — and are never served to a
    /// browser, so they are declared as such rather than being sniffed into something guessable.
    /// </summary>
    internal const string Artifact = "application/octet-stream";
}
