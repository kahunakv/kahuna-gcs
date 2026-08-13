using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// Building the provider, and the startup checks that decide whether a node comes up at all.
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
    [Fact]
    public async Task OptionsWithoutABucket_AreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => GcsBackupStorage.CreateProviderAsync(new GcsBackupStorageOptions()));
    }

    [Fact]
    public async Task OptionsNamingACredentialsFileThatIsNotThere_AreRefused()
    {
        GcsBackupStorageOptions options = new()
        {
            Bucket = "anything",
            CredentialsPath = Path.Combine(Path.GetTempPath(), "kahuna-missing-" + Guid.NewGuid().ToString("N"))
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => GcsBackupStorage.CreateProviderAsync(options));
    }

    [Fact]
    public async Task ABucketThatDoesNotExist_IsReportedRatherThanCreated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        GcsBackupStorageOptions options = Relaxed("kahuna-absent-" + Guid.NewGuid().ToString("N")[..12]);

        // A backup target must not create its own bucket: location, storage class, and access policy are
        // deployment decisions, and silently creating one would produce a bucket with none of them made.
        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => GcsBackupStorage.CreateProviderAsync(options));

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

        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => GcsBackupStorage.CreateProviderAsync(options));

        Assert.Contains("uniform bucket-level access", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABucketWithoutPublicAccessPrevention_IsRefusedByDefault()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        GcsBackupStorageOptions options = Relaxed(ctx.Bucket);
        options.RequirePublicAccessPrevention = true;

        GcsBackupStorageException ex = await Assert.ThrowsAsync<GcsBackupStorageException>(
            () => GcsBackupStorage.CreateProviderAsync(options));

        Assert.Contains("public access prevention", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAccessControlChecksCanBeWaivedDeliberately()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(Relaxed(ctx.Bucket));

        BackupStoragePair pair = provider(
            new BackupStorageContext(GcsBackupStorage.TargetName, "/var/lib/kahuna/backups", ctx.ScratchRoot));

        Assert.IsType<GcsBackupStorageTarget>(pair.Manifests);
        Assert.IsType<GcsBackupArtifactStore>(pair.Artifacts);
        Assert.True(pair.Artifacts.Capabilities.RequiresLocalScratch);
    }

    [Fact]
    public async Task AProviderWithNoScratchDirectory_RefusesToBuildAStore()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(Relaxed(ctx.Bucket));

        // A checkpoint can only be written through the filesystem, so a bucket target without a scratch
        // directory has nowhere to stage one. Failing here beats failing deep inside a checkpoint write.
        GcsBackupStorageException ex = Assert.Throws<GcsBackupStorageException>(() => provider(
            new BackupStorageContext(GcsBackupStorage.TargetName, "/var/lib/kahuna/backups", ScratchDir: null)));

        Assert.Contains("scratch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyingABucketDirectly_UsesTheSameChecks()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // A host may want to check its backup destination from a readiness probe without building a
        // provider, and that must not be a weaker check than the one startup runs.
        await GcsBackupStorage.VerifyBucketAsync(Relaxed(ctx.Bucket));

        GcsBackupStorageOptions strict = Relaxed(ctx.Bucket);
        strict.RequireUniformBucketLevelAccess = true;

        await Assert.ThrowsAsync<GcsBackupStorageException>(() => GcsBackupStorage.VerifyBucketAsync(strict));
    }

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
