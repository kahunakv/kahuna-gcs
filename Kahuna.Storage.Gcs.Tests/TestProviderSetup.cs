using Kahuna.Server.Configuration;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// Opening and starting the backup storage, and the startup checks that decide whether a node comes up at all.
/// <para>
/// These are the failures worth catching before a node is serving: a bucket that does not exist, one
/// whose access controls do not match what a backup destination needs, or a target with nowhere to
/// stage a checkpoint. Each of them otherwise surfaces at the first scheduled backup, which is
/// typically the moment nobody is watching.
/// </para>
/// </summary>
[Collection(GcsEmulatorCollection.Name)]
public class TestProviderSetup(GcsEmulatorFixture fixture)
{
    private static BackupStorageContext Context(string? scratchDir, IReadOnlyDictionary<string, string>? extensionOptions = null) => new()
    {
        BackupDir = "/var/lib/kahuna/backups",
        ScratchDir = scratchDir,
        Configuration = new KahunaConfiguration(),
        Logger = NullLogger.Instance,
        ExtensionOptions = extensionOptions ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kahuna-gcs-scratch");

    [Fact]
    public void TheProvider_AnswersToGcs_AndNeedsAScratchDirectory()
    {
        GcsBackupStorageProvider provider = new();

        Assert.Equal("gcs", provider.TargetName);
        Assert.Equal(GcsBackupStorageProvider.Name, provider.TargetName);
        Assert.True(provider.RequiresLocalScratch);
    }

    [Fact]
    public void OptionsWithoutABucket_AreRefused()
    {
        KahunaServerException ex = Assert.Throws<KahunaServerException>(
            () => new GcsBackupStorageProvider().Open(Context(Scratch)));

        Assert.Contains(GcsExtensionOptions.Bucket, ex.Message, StringComparison.Ordinal);

        Assert.Throws<KahunaServerException>(
            () => new GcsBackupStorageProvider(new GcsBackupStorageOptions()).Open(Context(Scratch)));
    }

    [Fact]
    public void AProviderWithNoScratchDirectory_RefusesToOpen()
    {
        // A checkpoint can only be written through the filesystem, so a bucket target without a scratch
        // directory has nowhere to stage one. Failing here beats failing deep inside a checkpoint write.
        KahunaServerException ex = Assert.Throws<KahunaServerException>(() =>
            new GcsBackupStorageProvider(new GcsBackupStorageOptions { Bucket = "b" }).Open(Context(scratchDir: null)));

        Assert.Contains("scratch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenDoesNoIo_AMissingCredentialsFileIsReportedWhenTheStorageStarts()
    {
        GcsBackupStorageOptions options = new()
        {
            Bucket = "anything",
            CredentialsPath = Path.Combine(Path.GetTempPath(), "kahuna-missing-" + Guid.NewGuid().ToString("N"))
        };

        using IBackupStorage storage = new GcsBackupStorageProvider(options).Open(Context(Scratch));

        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => storage.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStores_AreNotAvailableBeforeTheStart()
    {
        using IBackupStorage storage = new GcsBackupStorageProvider(new GcsBackupStorageOptions { Bucket = "b" }).Open(Context(Scratch));

        Assert.Throws<InvalidOperationException>(() => storage.Manifests);
        Assert.Throws<InvalidOperationException>(() => storage.Artifacts);
    }

    [Fact]
    public async Task ADisposedStorage_RefusesToStart()
    {
        IBackupStorage storage = new GcsBackupStorageProvider(new GcsBackupStorageOptions { Bucket = "b" }).Open(Context(Scratch));
        storage.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => storage.StartAsync(TestContext.Current.CancellationToken));
    }

    // ── the extension options ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachExtensionOption_SetsItsOption()
    {
        GcsBackupStorageOptions options = GcsExtensionOptions.Parse(new Dictionary<string, string>
        {
            ["gcs.bucket"] = "acme-backups",
            ["gcs.prefix"] = "prod/",
            ["gcs.credentials-path"] = "/etc/kahuna/key.json",
            ["gcs.require-uniform-bucket-level-access"] = "false",
            ["gcs.require-public-access-prevention"] = "False",
            ["gcs.deletion-policy-reaction"] = "fail",
            ["gcs.fail-on-manifest-id-collision"] = "false",
            ["gcs.resumable-upload-threshold-bytes"] = "1048576",
            ["gcs.resumable-chunk-size-bytes"] = "524288",
            ["gcs.emulator-base-uri"] = "http://localhost:4443/storage/v1/",
            // The keys of other extensions are not this target's business.
            ["solarium.environment"] = "io_uring",
        });

        Assert.Equal("acme-backups", options.Bucket);
        Assert.Equal("prod/", options.Prefix);
        Assert.Equal("/etc/kahuna/key.json", options.CredentialsPath);
        Assert.False(options.RequireUniformBucketLevelAccess);
        Assert.False(options.RequirePublicAccessPrevention);
        Assert.Equal(GcsDeletionPolicyReaction.Fail, options.DeletionPolicyReaction);
        Assert.False(options.FailOnManifestIdCollision);
        Assert.Equal(1_048_576, options.ResumableUploadThresholdBytes);
        Assert.Equal(524_288, options.ResumableChunkSizeBytes);
        Assert.Equal("http://localhost:4443/storage/v1/", options.EmulatorBaseUri);
    }

    [Fact]
    public void OnlyTheBucket_KeepsTheSafeDefaults()
    {
        GcsBackupStorageOptions options = GcsExtensionOptions.Parse(new Dictionary<string, string> { ["gcs.bucket"] = "b" });

        Assert.True(options.RequireUniformBucketLevelAccess);
        Assert.True(options.RequirePublicAccessPrevention);
        Assert.True(options.FailOnManifestIdCollision);
        Assert.Equal(GcsDeletionPolicyReaction.Warn, options.DeletionPolicyReaction);
        Assert.Null(options.CredentialsPath);
        Assert.Null(options.EmulatorBaseUri);
    }

    [Fact]
    public void AnUnknownGcsKey_IsRefused_SoATypoCannotTurnOffAProtection()
    {
        KahunaServerException ex = Assert.Throws<KahunaServerException>(() => GcsExtensionOptions.Parse(
            new Dictionary<string, string> { ["gcs.bucket"] = "b", ["gcs.require-uniform-access"] = "false" }));

        Assert.Contains("'gcs.require-uniform-access'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GcsExtensionOptions.RequireUniformBucketLevelAccess, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gcs.require-public-access-prevention", "yes-please")]
    [InlineData("gcs.fail-on-manifest-id-collision", "1")]
    [InlineData("gcs.resumable-chunk-size-bytes", "8MiB-secret")]
    [InlineData("gcs.resumable-upload-threshold-bytes", "1.5")]
    [InlineData("gcs.deletion-policy-reaction", "1")]
    public void ABadValue_IsRefused_ByItsKey_WithoutTheValue(string key, string value)
    {
        KahunaServerException ex = Assert.Throws<KahunaServerException>(() => GcsExtensionOptions.Parse(
            new Dictionary<string, string> { ["gcs.bucket"] = "b", [key] = value }));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message.Replace(key, "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderBuiltWithOptions_RefusesExtensionOptionsForTheSameSettings()
    {
        GcsBackupStorageProvider provider = new(new GcsBackupStorageOptions { Bucket = "b" });

        KahunaServerException ex = Assert.Throws<KahunaServerException>(() =>
            provider.Open(Context(Scratch, new Dictionary<string, string> { ["gcs.prefix"] = "prod/" })));
        Assert.Contains("'gcs.prefix'", ex.Message, StringComparison.Ordinal);

        // Another extension's key is fine.
        using IBackupStorage storage = provider.Open(Context(Scratch, new Dictionary<string, string> { ["solarium.environment"] = "x" }));
    }

    // ── the startup checks against the emulator ───────────────────────────────────────────────────────

    [Fact]
    public async Task ABucketThatDoesNotExist_IsReportedRatherThanCreated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        using IBackupStorage storage = Open(Relaxed("kahuna-absent-" + Guid.NewGuid().ToString("N")[..12]), Scratch);

        // A backup target must not create its own bucket: location, storage class, and access policy are
        // deployment decisions, and silently creating one would produce a bucket with none of them made.
        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => storage.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABucketWithoutUniformAccess_IsRefusedByDefault()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // The emulator models no access control, so its buckets report neither uniform bucket-level
        // access nor public access prevention — which makes it a faithful stand-in for the bucket this
        // check exists to reject.
        GcsBackupStorageOptions options = Relaxed(ctx.Bucket);
        options.RequireUniformBucketLevelAccess = true;

        using IBackupStorage storage = Open(options, ctx.ScratchRoot);
        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => storage.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("uniform bucket-level access", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => storage.Manifests);
    }

    [Fact]
    public async Task ABucketWithoutPublicAccessPrevention_IsRefusedByDefault()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        GcsBackupStorageOptions options = Relaxed(ctx.Bucket);
        options.RequirePublicAccessPrevention = true;

        using IBackupStorage storage = Open(options, ctx.ScratchRoot);
        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => storage.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("public access prevention", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAccessControlChecksCanBeWaivedDeliberately()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        using IBackupStorage storage = Open(Relaxed(ctx.Bucket), ctx.ScratchRoot);
        await storage.StartAsync(TestContext.Current.CancellationToken);

        Assert.IsType<GcsBackupStorageTarget>(storage.Manifests);
        Assert.IsType<GcsBackupArtifactStore>(storage.Artifacts);

        // A second start is a no-op.
        await storage.StartAsync(TestContext.Current.CancellationToken);
        Assert.IsType<GcsBackupStorageTarget>(storage.Manifests);
    }

    [Fact]
    public async Task TheExtensionOptions_OpenAndStartTheSameStorage()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        using IBackupStorage storage = new GcsBackupStorageProvider().Open(Context(ctx.ScratchRoot, new Dictionary<string, string>
        {
            [GcsExtensionOptions.Bucket] = ctx.Bucket,
            [GcsExtensionOptions.Prefix] = "prod/",
            [GcsExtensionOptions.EmulatorBaseUri] = fixture.Endpoint,
            [GcsExtensionOptions.RequireUniformBucketLevelAccess] = "false",
            [GcsExtensionOptions.RequirePublicAccessPrevention] = "false",
            [GcsExtensionOptions.DeletionPolicyReaction] = "ignore",
        }));

        await storage.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await storage.Manifests.ListManifestIdsAsync(TestContext.Current.CancellationToken));
    }

    private static IBackupStorage Open(GcsBackupStorageOptions options, string scratchDir) =>
        new GcsBackupStorageProvider(options).Open(Context(scratchDir));

    /// <summary>
    /// Options pointed at the emulator with the access-control checks waived, because the emulator
    /// authenticates no one and reports no bucket policy. Every test that needs those checks to fire
    /// turns them back on explicitly.
    /// </summary>
    private GcsBackupStorageOptions Relaxed(string bucket) => new()
    {
        Bucket = bucket,
        Prefix = "prod/",
        EmulatorBaseUri = fixture.Endpoint,
        RequireUniformBucketLevelAccess = false,
        RequirePublicAccessPrevention = false,
        DeletionPolicyReaction = GcsDeletionPolicyReaction.Ignore
    };
}
