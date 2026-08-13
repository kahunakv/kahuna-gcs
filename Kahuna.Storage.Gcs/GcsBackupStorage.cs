using Google.Apis.Auth.OAuth2;
using Google.Apis.Json;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Entry point for backing Kahuna's point-in-time recovery with a Google Cloud Storage bucket.
/// <para>
/// A host installs it by assigning the returned provider to <c>KahunaConfiguration.BackupStorageProvider</c>
/// (or <c>EmbeddedKahunaOptions.BackupStorageProvider</c>) and setting the backup target to
/// <see cref="TargetName"/>. Kahuna's core never references this package — an embedded consumer inherits
/// whatever the core depends on, so a cloud SDK belongs behind the extension point rather than inside it.
/// </para>
/// <example>
/// <code>
/// BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(
///     new GcsBackupStorageOptions { Bucket = "acme-kahuna-backups", Prefix = "prod/" }, logger);
///
/// configuration.BackupTarget = GcsBackupStorage.TargetName;
/// configuration.BackupScratchDir = "/var/lib/kahuna/backup-scratch";
/// configuration.BackupStorageProvider = provider;
/// </code>
/// </example>
/// </summary>
public static class GcsBackupStorage
{
    /// <summary>
    /// Value the backup target must be set to for this provider to be selected. Kahuna refuses to start
    /// with a non-local target and no provider registered, so a typo here surfaces at startup rather
    /// than as backups quietly going to local disk.
    /// </summary>
    public const string TargetName = "gcs";

    /// <summary>
    /// Builds a storage provider after verifying the bucket is usable and configured the way a backup
    /// target requires.
    /// <para>
    /// The verification is done here, once, rather than on first use: a node whose backup destination is
    /// unreachable or unprotected should fail to start, not come up healthy and discover it at the first
    /// scheduled backup — which is typically the moment nobody is watching.
    /// </para>
    /// </summary>
    /// <param name="options">Bucket, credentials, transfer sizing, and which bucket protections are mandatory.</param>
    /// <param name="logger">Receives warnings about bucket settings that defeat reclamation. Optional.</param>
    /// <param name="ct">Cancels the startup verification.</param>
    public static async Task<BackupStorageProvider> CreateProviderAsync(
        GcsBackupStorageOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        ILogger effectiveLogger = logger ?? NullLogger.Instance;

        StorageClient client = await CreateClientAsync(options, ct).ConfigureAwait(false);
        await GcsBucketVerifier.VerifyAsync(client, options.Bucket, options, effectiveLogger, ct).ConfigureAwait(false);

        GcsObjectLayout layout = new(options.Prefix);
        int chunkSize = RoundUpToChunkBoundary(options.ResumableChunkSizeBytes);

        return context =>
        {
            // The scratch directory is mandatory and Kahuna validates it at startup, but a host can
            // construct a configuration without going through that path — and the failure it would
            // otherwise produce is an unhelpful one deep inside a checkpoint write.
            if (string.IsNullOrWhiteSpace(context.ScratchDir))
                throw new GcsBackupStorageException(
                    "The Google Cloud Storage backup target stages every checkpoint through a local directory, " +
                    "because a persistence backend can only write one through the filesystem. Configure a backup " +
                    "scratch directory and size it for one full backup, which transits it in its entirety.");

            return new BackupStoragePair(
                new GcsBackupStorageTarget(client, options.Bucket, layout, options.FailOnManifestIdCollision),
                new GcsBackupArtifactStore(
                    client,
                    options.Bucket,
                    layout,
                    context.ScratchDir,
                    options.ResumableUploadThresholdBytes,
                    chunkSize,
                    effectiveLogger));
        };
    }

    /// <summary>
    /// Verifies the bucket without building a provider, for a host that wants to check its backup
    /// destination as part of a readiness probe or a configuration lint.
    /// </summary>
    public static async Task VerifyBucketAsync(
        GcsBackupStorageOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        StorageClient client = await CreateClientAsync(options, ct).ConfigureAwait(false);
        await GcsBucketVerifier.VerifyAsync(client, options.Bucket, options, logger ?? NullLogger.Instance, ct)
            .ConfigureAwait(false);
    }

    private static async Task<StorageClient> CreateClientAsync(GcsBackupStorageOptions options, CancellationToken ct)
    {
        StorageClientBuilder builder = new();

        if (options.CredentialsPath is not null)
            builder.Credential = await LoadServiceAccountCredentialAsync(options.CredentialsPath, ct)
                .ConfigureAwait(false);

        if (options.EmulatorBaseUri is not null)
        {
            builder.BaseUri = options.EmulatorBaseUri;
            builder.UnauthenticatedAccess = true;
        }

        // With neither set, the client resolves Application Default Credentials: workload identity on
        // GKE, the metadata server on GCE, and gcloud user credentials on a developer machine. That is
        // the intended path — it is the only one that works without a long-lived secret.
        return await builder.BuildAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a service-account key file as an explicit credential.
    /// <para>
    /// The file is parsed here, and rejected unless it really is a service-account key, rather than
    /// being handed to the SDK's path-based loading. A credentials file can also describe an external
    /// account, which tells the client to fetch tokens from a URL named inside the file — so a file
    /// swapped underneath a node could redirect authentication somewhere of an attacker's choosing.
    /// Narrowing what this accepts closes that off; nothing that legitimately needs it is lost, because
    /// the intended path for every non-key deployment is Application Default Credentials.
    /// </para>
    /// </summary>
    private static async Task<GoogleCredential> LoadServiceAccountCredentialAsync(string path, CancellationToken ct)
    {
        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

        JsonCredentialParameters? parameters;
        try
        {
            parameters = NewtonsoftJsonSerializer.Instance.Deserialize<JsonCredentialParameters>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GcsBackupStorageException(
                "The configured Google Cloud credentials file is not valid JSON credentials.", ex);
        }

        if (parameters is null
            || parameters.Type != JsonCredentialParameters.ServiceAccountCredentialType
            || string.IsNullOrEmpty(parameters.ClientEmail)
            || string.IsNullOrEmpty(parameters.PrivateKey))
        {
            throw new GcsBackupStorageException(
                "The configured Google Cloud credentials file is not a service-account key. Only service-account " +
                "keys are accepted as an explicit credential; for every other environment use Application Default " +
                "Credentials, which is workload identity on GKE and the metadata server on GCE.");
        }

        ServiceAccountCredential.Initializer initializer = new(parameters.ClientEmail)
        {
            ProjectId = parameters.ProjectId
        };

        return GoogleCredential.FromServiceAccountCredential(
            new ServiceAccountCredential(initializer.FromPrivateKey(parameters.PrivateKey)));
    }

    /// <summary>
    /// Rounds a chunk size up to the 256 KiB boundary Google's resumable upload protocol requires, so a
    /// plausible-looking configured value is corrected rather than rejected at the first large upload.
    /// </summary>
    private static int RoundUpToChunkBoundary(int chunkSizeBytes)
    {
        const int boundary = 256 * 1024;
        int chunks = (chunkSizeBytes + boundary - 1) / boundary;
        return Math.Max(1, chunks) * boundary;
    }
}
