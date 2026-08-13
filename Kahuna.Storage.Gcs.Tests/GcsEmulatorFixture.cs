using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// Shared connection to a local GCS stand-in (<c>docker compose up -d</c>).
/// <para>
/// The suite is skipped rather than failed when nothing is listening, so a machine without Docker
/// still gets a green run from the tests that need no server. What that costs is worth stating: a
/// skipped suite proves nothing, and the emulator itself proves nothing about credentials, IAM, or
/// bucket policy — it speaks the object protocol and authenticates no one.
/// </para>
/// </summary>
public sealed class GcsEmulatorFixture : IAsyncLifetime
{
    private const string EndpointVariable = "KAHUNA_GCS_TEST_ENDPOINT";
    private const string DefaultEndpoint = "http://localhost:4443/storage/v1/";

    internal const string ProjectId = "kahuna-storage-tests";

    /// <summary>Non-null when the emulator could not be reached, and therefore why the suite skipped.</summary>
    public string? SkipReason { get; private set; }

    public StorageClient Client { get; private set; } = null!;

    public string Endpoint { get; } =
        Environment.GetEnvironmentVariable(EndpointVariable) ?? DefaultEndpoint;

    public async ValueTask InitializeAsync()
    {
        try
        {
            StorageClientBuilder builder = new()
            {
                BaseUri = Endpoint,
                UnauthenticatedAccess = true
            };

            StorageClient client = await builder.BuildAsync().ConfigureAwait(false);

            // A probe request, so an unreachable server is reported once here rather than as an
            // unrelated-looking failure inside whichever test ran first.
            using CancellationTokenSource probeTimeout = new(TimeSpan.FromSeconds(10));
            await client.ListBucketsAsync(ProjectId).GetAsyncEnumerator(probeTimeout.Token).MoveNextAsync()
                .ConfigureAwait(false);

            Client = client;
        }
        catch (Exception ex) when (ex is HttpRequestException or GoogleApiException or OperationCanceledException
                                       or TaskCanceledException)
        {
            SkipReason =
                $"No Google Cloud Storage emulator at {Endpoint}. Start one with 'docker compose up -d', or point " +
                $"{EndpointVariable} at another endpoint.";
        }
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class GcsEmulatorCollection : ICollectionFixture<GcsEmulatorFixture>
{
    public const string Name = "gcs-emulator";
}

/// <summary>
/// One test's private bucket, scratch directory, and pair of stores. Every test gets a fresh bucket so
/// listings, sweeps, and prefix deletes cannot see another test's keys — several of the behaviours
/// under test are about what a listing returns, and a shared bucket would make those assertions depend
/// on execution order.
/// </summary>
internal sealed class GcsTestContext : IAsyncDisposable
{
    private readonly string _scratchRoot;

    private GcsTestContext(
        StorageClient client,
        string bucket,
        GcsObjectLayout layout,
        GcsBackupStorageTarget manifests,
        GcsBackupArtifactStore artifacts,
        string scratchRoot)
    {
        Client = client;
        Bucket = bucket;
        Layout = layout;
        Manifests = manifests;
        Artifacts = artifacts;
        _scratchRoot = scratchRoot;
    }

    internal StorageClient Client { get; }
    internal string Bucket { get; }
    internal GcsObjectLayout Layout { get; }
    internal GcsBackupStorageTarget Manifests { get; }
    internal GcsBackupArtifactStore Artifacts { get; }
    internal string ScratchRoot => _scratchRoot;

    internal static async Task<GcsTestContext> CreateAsync(
        GcsEmulatorFixture fixture,
        string prefix = "prod/",
        bool failOnManifestIdCollision = true,
        int uploadThresholdBytes = 8 * 1024 * 1024,
        int uploadChunkSizeBytes = 256 * 1024)
    {
        string bucket = "kahuna-test-" + Guid.NewGuid().ToString("N")[..12];
        await fixture.Client.CreateBucketAsync(GcsEmulatorFixture.ProjectId, bucket).ConfigureAwait(false);

        string scratchRoot = Path.Combine(Path.GetTempPath(), "kahuna-gcs-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(scratchRoot);

        GcsObjectLayout layout = new(prefix);

        return new GcsTestContext(
            fixture.Client,
            bucket,
            layout,
            new GcsBackupStorageTarget(fixture.Client, bucket, layout, failOnManifestIdCollision),
            new GcsBackupArtifactStore(
                fixture.Client, bucket, layout, scratchRoot, uploadThresholdBytes, uploadChunkSizeBytes,
                NullLogger.Instance),
            scratchRoot);
    }

    /// <summary>Every key currently in the bucket, in name order.</summary>
    internal async Task<List<string>> ListAllKeysAsync()
    {
        List<string> keys = [];
        await foreach (Google.Apis.Storage.v1.Data.Object item in Client.ListObjectsAsync(Bucket, ""))
            keys.Add(item.Name);

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    internal async Task PutRawAsync(string key, byte[] content)
    {
        using MemoryStream source = new(content, writable: false);
        await Client.UploadObjectAsync(Bucket, key, "application/octet-stream", source).ConfigureAwait(false);
    }

    internal async Task<byte[]> GetRawAsync(string key)
    {
        using MemoryStream buffer = new();
        await Client.DownloadObjectAsync(Bucket, key, buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
                Directory.Delete(_scratchRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not invalidate a result.
        }

        try
        {
            await Client.DeleteBucketAsync(Bucket, new DeleteBucketOptions { DeleteObjects = true })
                .ConfigureAwait(false);
        }
        catch (GoogleApiException)
        {
            // The emulator is memory-backed and torn down with the container.
        }
    }
}
