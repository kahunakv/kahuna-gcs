namespace Kahuna.Storage.Gcs;

/// <summary>
/// Thrown when the Google Cloud Storage backup target cannot be used as configured: the bucket is
/// absent or invisible to the credentials, or it lacks a protection the target refuses to run without.
/// <para>
/// Distinct from the artifact-integrity failures the backup path already models, because this is a
/// deployment problem rather than a corrupt or missing artifact — and it is raised at startup, so a
/// node fails to come up instead of advertising a backup capability it does not have.
/// </para>
/// </summary>
public sealed class GcsBackupStorageException : Exception
{
    /// <summary>Creates the exception with an operator-facing explanation of what is misconfigured.</summary>
    public GcsBackupStorageException(string message) : base(message) { }

    /// <summary>Creates the exception, preserving the Google API failure that revealed the problem.</summary>
    public GcsBackupStorageException(string message, Exception innerException) : base(message, innerException) { }
}
