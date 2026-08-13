using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Bucket = Google.Apis.Storage.v1.Data.Bucket;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Checks, at startup, that the bucket is configured the way a backup target needs it — rather than
/// documenting the requirements and discovering at restore time that none were applied.
/// <para>
/// Two classes of setting are checked, and they fail differently on purpose. Access controls (uniform
/// bucket-level access, public access prevention) are the bucket-scale stand-in for the file modes the
/// local target enforces, and a target that silently ran without them would be dropping a security
/// control the local target treats as mandatory — so those refuse to start. Deletion-affecting
/// settings (versioning, soft delete, retention policy) instead make deletes not delete: retention and
/// garbage collection run, report success, and reclaim nothing. That is a cost and compliance problem
/// rather than an exposure, and GCS turns soft delete on for new buckets by default, so it is reported
/// rather than refused unless an operator asks for the stricter behaviour.
/// </para>
/// </summary>
internal static class GcsBucketVerifier
{
    private const string PublicAccessPreventionEnforced = "enforced";

    internal static async Task VerifyAsync(
        StorageClient client, string bucketName, GcsBackupStorageOptions options, ILogger logger, CancellationToken ct)
    {
        Bucket bucket;

        try
        {
            bucket = await client.GetBucketAsync(bucketName, options: null, ct).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (GcsErrors.IsNotFound(ex))
        {
            throw new GcsBackupStorageException(
                $"Backup bucket '{bucketName}' does not exist, or the configured credentials cannot see it. " +
                "Kahuna does not create backup buckets: the bucket's location, storage class, and access " +
                "policy are deployment decisions.", ex);
        }

        VerifyAccessControls(bucket, bucketName, options);
        ReportDeletionPolicies(bucket, bucketName, options, logger);
    }

    private static void VerifyAccessControls(Bucket bucket, string bucketName, GcsBackupStorageOptions options)
    {
        if (options.RequireUniformBucketLevelAccess &&
            bucket.IamConfiguration?.UniformBucketLevelAccess?.Enabled != true)
        {
            throw new GcsBackupStorageException(
                $"Backup bucket '{bucketName}' does not have uniform bucket-level access enabled. Backup " +
                "manifests name every artifact and its digest, and with object ACLs still active an object can " +
                "carry access grants the bucket policy does not describe. Enable uniform bucket-level access, " +
                "or set RequireUniformBucketLevelAccess to false to accept that risk deliberately.");
        }

        if (options.RequirePublicAccessPrevention &&
            !string.Equals(
                bucket.IamConfiguration?.PublicAccessPrevention, PublicAccessPreventionEnforced,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GcsBackupStorageException(
                $"Backup bucket '{bucketName}' does not enforce public access prevention. Backup artifacts are a " +
                "complete copy of the cluster's committed state, so a bucket that can be made public by a later " +
                "IAM edit is one edit away from publishing it. Enforce public access prevention, or set " +
                "RequirePublicAccessPrevention to false to accept that risk deliberately.");
        }
    }

    private static void ReportDeletionPolicies(
        Bucket bucket, string bucketName, GcsBackupStorageOptions options, ILogger logger)
    {
        if (options.DeletionPolicyReaction == GcsDeletionPolicyReaction.Ignore)
            return;

        List<string> findings = [];

        if (bucket.Versioning?.Enabled == true)
            findings.Add("object versioning is enabled, so a deleted artifact is retained as a noncurrent version");

        if (bucket.SoftDeletePolicy?.RetentionDurationSeconds is > 0)
            findings.Add(
                "a soft-delete policy is set, so a deleted artifact stays recoverable and billable for " +
                $"{bucket.SoftDeletePolicy.RetentionDurationSeconds} seconds");

        if (bucket.RetentionPolicy?.RetentionPeriod is > 0)
            findings.Add(
                "a retention policy is set, so artifacts cannot be deleted until their retention period expires");

        if (findings.Count == 0)
            return;

        string detail = string.Join("; ", findings);

        if (options.DeletionPolicyReaction == GcsDeletionPolicyReaction.Fail)
        {
            throw new GcsBackupStorageException(
                $"Backup bucket '{bucketName}' keeps deleted objects alive: {detail}. Retention and garbage " +
                "collection will report reclaimed bytes that were never actually freed. Remove the setting, or " +
                "set DeletionPolicyReaction to Warn or Ignore to proceed knowing reclamation is ineffective.");
        }

        logger.LogWarning(
            "Backup bucket {Bucket} keeps deleted objects alive: {Detail}. Backup retention and garbage " +
            "collection will delete objects successfully without reclaiming the storage they occupy",
            bucketName, detail);
    }
}
