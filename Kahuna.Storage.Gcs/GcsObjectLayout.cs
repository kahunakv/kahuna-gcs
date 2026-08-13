using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Maps backup identities onto object keys, and back.
/// <para>
/// Manifests and artifacts share one prefix, exactly as they share one directory on the local target.
/// That means the sweep cannot tell them apart by container and has to classify each key by name, so
/// the classification lives here in one place: getting it wrong in either direction is destructive —
/// a manifest read as an artifact gets reclaimed, an artifact read as a manifest blinds the sweep to
/// bytes it should have freed.
/// </para>
/// </summary>
internal sealed class GcsObjectLayout
{
    internal const string ManifestSuffix = ".manifest";

    /// <summary>
    /// Prefix the driver asks to have a checkpoint published under, and to have materialised back out
    /// of on restore. It is fixed by the calling convention rather than chosen here, so changing it
    /// would strand every checkpoint already in the bucket.
    /// </summary>
    internal const string CheckpointPrefix = "checkpoint";

    private readonly string _prefix;

    internal GcsObjectLayout(string prefix)
    {
        _prefix = Normalize(prefix);
    }

    /// <summary>The prefix every key produced here starts with; empty for the bucket root.</summary>
    internal string RootPrefix => _prefix;

    /// <summary>
    /// Trims a configured prefix into the form GCS keys use: no leading slash, exactly one trailing
    /// slash, or empty for the bucket root.
    /// </summary>
    private static string Normalize(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "";

        string trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? "" : trimmed + "/";
    }

    internal string ManifestKey(Guid backupId) => _prefix + backupId.ToString("N") + ManifestSuffix;

    /// <summary>Prefix covering every artifact of one backup.</summary>
    internal string ArtifactPrefix(Guid backupId) => _prefix + backupId.ToString("N") + "/";

    /// <summary>
    /// Prefix covering the artifacts under <paramref name="relativePrefix"/> within one backup. An
    /// empty relative prefix covers the whole backup.
    /// </summary>
    internal string ArtifactPrefix(Guid backupId, string relativePrefix)
    {
        string relative = relativePrefix.Trim('/');
        return relative.Length == 0
            ? ArtifactPrefix(backupId)
            : ArtifactPrefix(backupId) + relative + "/";
    }

    internal string ArtifactKey(Guid backupId, string relativePath) =>
        ArtifactPrefix(backupId) + NormalizeRelativePath(backupId, relativePath);

    /// <summary>
    /// Rejects a relative path that would address something other than an artifact of this backup.
    /// <para>
    /// A bucket has no directory tree to escape, so <c>..</c> cannot walk out of a prefix the way it
    /// walks out of a directory — but it would still produce a key nothing else can address or
    /// reclaim, and the local target refuses the same shapes. Keeping both targets strict means a
    /// path that works on one works on the other, rather than a backup succeeding on GCS and failing
    /// the day it is taken against a directory.
    /// </para>
    /// </summary>
    internal static string NormalizeRelativePath(Guid backupId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new BackupArtifactException($"Backup {backupId:N}: empty artifact path.");

        string normalized = relativePath.Replace('\\', '/');

        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new BackupArtifactException($"Backup {backupId:N}: unsafe artifact path '{relativePath}'.");

        foreach (string segment in normalized.Split('/'))
        {
            if (segment is ".." or ".")
                throw new BackupArtifactException(
                    $"Backup {backupId:N}: artifact '{relativePath}' escapes the artifact root.");
        }

        if (normalized.Contains("//", StringComparison.Ordinal))
            throw new BackupArtifactException($"Backup {backupId:N}: unsafe artifact path '{relativePath}'.");

        return normalized;
    }

    /// <summary>
    /// Recovers a backup id from a manifest key. Returns false for any key that is not a manifest of
    /// this layout — an artifact key, a manifest under a different prefix, or a name that does not
    /// parse as an id.
    /// </summary>
    internal bool TryParseManifestKey(string key, out Guid backupId)
    {
        backupId = Guid.Empty;

        if (!key.StartsWith(_prefix, StringComparison.Ordinal))
            return false;

        string name = key[_prefix.Length..];

        // A manifest key is a single flat name. Anything containing a separator is an artifact, even
        // if it happens to end in the manifest suffix.
        if (name.Contains('/'))
            return false;

        if (!name.EndsWith(ManifestSuffix, StringComparison.Ordinal))
            return false;

        return Guid.TryParseExact(name[..^ManifestSuffix.Length], "N", out backupId);
    }

    /// <summary>
    /// Recovers a backup id from an artifact key. Returns false for a manifest key, or for a key whose
    /// first segment does not parse as an id.
    /// </summary>
    internal bool TryParseArtifactKey(string key, out Guid backupId)
    {
        backupId = Guid.Empty;

        if (!key.StartsWith(_prefix, StringComparison.Ordinal))
            return false;

        string remainder = key[_prefix.Length..];
        int slash = remainder.IndexOf('/');
        if (slash <= 0)
            return false;

        return Guid.TryParseExact(remainder[..slash], "N", out backupId);
    }

    /// <summary>Strips a prefix from a key, yielding the artifact-relative path under it.</summary>
    internal static string RelativeTo(string prefix, string key) =>
        key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
}
