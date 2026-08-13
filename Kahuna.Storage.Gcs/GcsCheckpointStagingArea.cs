using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// A local directory a persistence backend checkpoints into, published to the bucket on commit.
/// <para>
/// This indirection exists because a backend writes a checkpoint through the filesystem and can target
/// nothing else, so a bucket can never be the direct destination. The local target hands back its own
/// final artifact directory and commits as a no-op; here the checkpoint is written to scratch, uploaded
/// under the backup's <c>checkpoint/</c> prefix, and the scratch tree dropped.
/// </para>
/// <para>
/// The upload happens before the manifest is published, which is what preserves the ordering the whole
/// protocol rests on: artifacts first, manifest last, so a crash mid-backup leaves orphaned bytes the
/// sweep reclaims rather than a manifest pointing at artifacts that do not exist.
/// </para>
/// </summary>
internal sealed class GcsCheckpointStagingArea : IBackupCheckpointStagingArea
{
    private readonly GcsBackupArtifactStore _store;
    private readonly Guid _backupId;
    private readonly string _scratchParent;

    private bool _disposed;

    internal GcsCheckpointStagingArea(
        GcsBackupArtifactStore store, Guid backupId, string localPath, string scratchParent)
    {
        _store = store;
        _backupId = backupId;
        _scratchParent = scratchParent;
        LocalPath = localPath;
    }

    public string LocalPath { get; }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(LocalPath))
            throw new BackupArtifactException(
                $"Backup {_backupId:N}: the staged checkpoint directory is missing, so there is nothing to publish.");

        foreach (string file in Directory.EnumerateFiles(LocalPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(LocalPath, file).Replace(Path.DirectorySeparatorChar, '/');

            await _store.UploadFileAsync(
                _backupId, GcsObjectLayout.CheckpointPrefix + "/" + relative, file, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        // Scratch is transient whatever the outcome. Leaving it behind is worse than losing it: a later
        // run finding a checkpoint tree at a path it expects to create would either fail the backend's
        // publishing rename or, worse, look like valid staged input.
        try
        {
            if (Directory.Exists(_scratchParent))
                Directory.Delete(_scratchParent, recursive: true);
        }
        catch (IOException ex)
        {
            _store.Logger.LogWarning(
                ex, "Could not remove the backup scratch directory for {BackupId:N} after staging", _backupId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _store.Logger.LogWarning(
                ex, "Could not remove the backup scratch directory for {BackupId:N} after staging", _backupId);
        }

        return ValueTask.CompletedTask;
    }
}
