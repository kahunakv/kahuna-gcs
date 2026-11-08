using Google.Apis.Auth.OAuth2;
using Google.Apis.Json;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// The backup storage of one node in a Google Cloud Storage bucket, from <see cref="GcsBackupStorageProvider.Open"/>.
/// <para>
/// <see cref="StartAsync"/> builds the client, resolves the credentials and checks the bucket. The checks run
/// here, one time, before the node joins its cluster: a node whose backup destination is unreachable or
/// unprotected must fail to start, not come up healthy and discover it at the first scheduled backup, which is
/// typically the moment nobody is watching.
/// </para>
/// </summary>
internal sealed class GcsBackupStorage(GcsBackupStorageOptions options, string scratchDir, ILogger logger) : IBackupStorage
{
    // Guards the publication of the started stores against Dispose.
    private readonly Lock sync = new();

    private StorageClient? client;

    private GcsBackupStorageTarget? manifests;

    private GcsBackupArtifactStore? artifacts;

    private bool disposed;

    public IBackupStorageTarget Manifests =>
        manifests ?? throw new InvalidOperationException("The Google Cloud Storage backup target is not started.");

    public IBackupArtifactStore Artifacts =>
        artifacts ?? throw new InvalidOperationException("The Google Cloud Storage backup target is not started.");

    public async Task StartAsync(CancellationToken ct)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (manifests is not null)
                return;
        }

        StorageClient built = await CreateClientAsync(options, ct).ConfigureAwait(false);

        try
        {
            await GcsBucketVerifier.VerifyAsync(built, options.Bucket, options, logger, ct).ConfigureAwait(false);
        }
        catch
        {
            built.Dispose();
            throw;
        }

        GcsObjectLayout layout = new(options.Prefix);

        lock (sync)
        {
            // A dispose during the start, or a start that ran at the same time and completed first: this
            // client has no owner.
            if (disposed || manifests is not null)
            {
                built.Dispose();
                ObjectDisposedException.ThrowIf(disposed, this);
                return;
            }

            client = built;
            artifacts = new GcsBackupArtifactStore(
                built,
                options.Bucket,
                layout,
                scratchDir,
                options.ResumableUploadThresholdBytes,
                RoundUpToChunkBoundary(options.ResumableChunkSizeBytes),
                logger);
            manifests = new GcsBackupStorageTarget(built, options.Bucket, layout, options.FailOnManifestIdCollision);
        }
    }

    public void Dispose()
    {
        StorageClient? started;

        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            started = client;
            client = null;
        }

        started?.Dispose();
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
        if (!File.Exists(path))
            throw new GcsBackupStorageException("The configured Google Cloud credentials file does not exist.");

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
