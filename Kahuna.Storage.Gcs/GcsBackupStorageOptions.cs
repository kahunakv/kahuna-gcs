namespace Kahuna.Storage.Gcs;

/// <summary>
/// Everything the Google Cloud Storage backup target needs: which bucket to write to, how to
/// authenticate, and which bucket-level protections it refuses to start without.
/// </summary>
public sealed class GcsBackupStorageOptions
{
    /// <summary>
    /// Name of the bucket holding manifests and artifacts. Required.
    /// </summary>
    public string Bucket { get; set; } = "";

    /// <summary>
    /// Key prefix every object is written under, so one bucket can host several clusters or share
    /// space with unrelated data. Empty means the bucket root. A trailing slash is added when absent,
    /// and a leading slash is stripped — GCS keys have no leading separator, and one written by hand
    /// would produce an object whose name starts with an empty path segment.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Path to a service-account JSON key file. Leave null to use Application Default Credentials,
    /// which is the intended path: workload identity on GKE, the metadata server on GCE, and
    /// <c>gcloud</c> user credentials on a developer machine. An explicit file is a fallback for
    /// environments where ADC cannot be arranged, and it is a long-lived secret on local disk — prefer
    /// workload identity wherever it is available.
    /// </summary>
    public string? CredentialsPath { get; set; }

    /// <summary>
    /// Bytes an artifact may reach before it is streamed rather than held in memory.
    /// <para>
    /// Every upload uses Google's resumable protocol — the .NET client has no single-request upload
    /// path — so this does not select a protocol. What it selects is memory: an artifact under the
    /// threshold is buffered and handed over whole, so its length is known up front and it goes in one
    /// chunk, while a larger one streams through in <see cref="ResumableChunkSizeBytes"/> chunks and is
    /// never resident. Raising it trades memory per concurrent upload for fewer round trips on
    /// mid-sized artifacts; it has no effect on a multi-gigabyte checkpoint file, which always streams.
    /// Default 8 MiB.
    /// </para>
    /// </summary>
    public int ResumableUploadThresholdBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Chunk size for artifacts that stream. Google's upload protocol requires a multiple of 256 KiB;
    /// a value that is not one is rounded up. Larger chunks mean fewer round trips and more memory
    /// held per in-flight upload. Default 8 MiB.
    /// </summary>
    public int ResumableChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Refuse to start unless the bucket has uniform bucket-level access enabled.
    /// <para>
    /// This is the bucket-scale equivalent of a guarantee the local target already makes: it restricts
    /// a manifest to owner-only <i>before</i> publishing it, so a manifest — which names every artifact
    /// and its digest — is never briefly readable by anyone else. With object ACLs still active, an
    /// object can carry its own access grants and that guarantee has no equivalent. Verifying the
    /// setting costs one metadata read at startup; documenting it instead means discovering it was
    /// never applied only after the bytes are exposed.
    /// </para>
    /// </summary>
    public bool RequireUniformBucketLevelAccess { get; set; } = true;

    /// <summary>
    /// Refuse to start unless the bucket enforces public access prevention. Backup artifacts are a
    /// complete copy of the cluster's committed state; a bucket that can be made public by a later
    /// IAM edit is one edit away from publishing it.
    /// </summary>
    public bool RequirePublicAccessPrevention { get; set; } = true;

    /// <summary>
    /// How to react when the bucket has object versioning, a soft-delete policy, or a retention policy
    /// enabled. Each of these redefines what deletion means: a deleted object may remain billable and
    /// recoverable, so retention and garbage collection reclaim nothing even though every delete
    /// succeeded. Default is to log a warning, because GCS enables a soft-delete policy on new buckets
    /// by default and refusing outright would reject most buckets an operator creates.
    /// </summary>
    public GcsDeletionPolicyReaction DeletionPolicyReaction { get; set; } = GcsDeletionPolicyReaction.Warn;

    /// <summary>
    /// Publish a manifest with a create-if-absent precondition, so writing one whose id already exists
    /// fails loudly instead of overwriting it.
    /// <para>
    /// This is deliberately stricter than the local target, which publishes with an overwriting rename
    /// because a filesystem offers nothing better. A backup id is a fresh GUID per backup, so a
    /// collision means something is wrong — two nodes sharing one bucket prefix, or a restored bucket
    /// replayed over a live one — and silently overwriting the manifest would destroy the record of the
    /// backup that was already there. Turn it off only if a deployment genuinely needs to rewrite a
    /// manifest in place.
    /// </para>
    /// </summary>
    public bool FailOnManifestIdCollision { get; set; } = true;

    /// <summary>
    /// Base address of a GCS-compatible endpoint, for running against an emulator. Null uses the real
    /// service. An emulator exercises the object protocol only — never credentials, IAM, or bucket
    /// policy — so a deployment verified solely against one has verified none of its access control.
    /// </summary>
    public string? EmulatorBaseUri { get; set; }

    /// <summary>
    /// Throws when the options cannot produce a working target, at startup rather than at the first
    /// backup.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
            throw new ArgumentException("A Google Cloud Storage backup target requires a bucket name.", nameof(Bucket));

        if (ResumableUploadThresholdBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ResumableUploadThresholdBytes), "The resumable upload threshold cannot be negative.");

        if (ResumableChunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(ResumableChunkSizeBytes), "The resumable chunk size must be positive.");

        if (CredentialsPath is not null && !File.Exists(CredentialsPath))
            throw new FileNotFoundException(
                "The configured Google Cloud credentials file does not exist.", CredentialsPath);
    }
}

/// <summary>
/// What the target does when the bucket carries a setting that keeps deleted objects alive, and so
/// prevents retention and garbage collection from actually reclaiming anything.
/// </summary>
public enum GcsDeletionPolicyReaction
{
    /// <summary>Report it and keep going. Reclamation may be ineffective; the operator has been told.</summary>
    Warn = 0,

    /// <summary>Refuse to start. Choose this where storage cost or an erasure obligation depends on deletes taking effect.</summary>
    Fail = 1,

    /// <summary>Do not check. The operator has established the behaviour by other means.</summary>
    Ignore = 2
}
