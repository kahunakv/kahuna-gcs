using System.Text;
using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// The manifest half of the target, driven against a real GCS client.
/// <para>
/// The behaviours worth pinning down here are the ones the backup protocol depends on rather than the
/// round trip: a manifest is the existence predicate for a backup, so what a listing does when an
/// object is unreadable, and what a publish does when an id already exists, decide whether artifacts
/// get reclaimed or overwritten.
/// </para>
/// </summary>
[Collection(GcsEmulatorCollection.Name)]
public class TestManifestTarget(GcsEmulatorFixture fixture)
{
    [Fact]
    public async Task Manifest_RoundTripsThroughTheBucket()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        BackupManifest manifest = BackupManifest.CreateFull([]);
        manifest.ClusterId = "acme-prod";
        manifest.Checksums["checkpoint/000012.sst"] = new string('a', 64);
        manifest.Sizes["checkpoint/000012.sst"] = 4096;

        await ctx.Manifests.PutAsync(manifest);

        BackupManifest? read = await ctx.Manifests.GetAsync(manifest.BackupId);

        Assert.NotNull(read);
        Assert.Equal(manifest.BackupId, read.BackupId);
        Assert.Equal(BackupType.Full, read.Type);
        Assert.Equal("acme-prod", read.ClusterId);
        Assert.Equal(4096, read.Sizes["checkpoint/000012.sst"]);
    }

    [Fact]
    public async Task Manifest_ThatWasNeverWritten_ReadsAsAbsentRatherThanThrowing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Assert.Null(await ctx.Manifests.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeletingAManifestTwice_Succeeds()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        BackupManifest manifest = BackupManifest.CreateFull([]);
        await ctx.Manifests.PutAsync(manifest);

        await ctx.Manifests.DeleteAsync(manifest.BackupId);

        // Callers re-run a delete to converge after a partial failure, so the second pass has to be a
        // no-op rather than an error — otherwise a half-finished reclamation can never finish.
        await ctx.Manifests.DeleteAsync(manifest.BackupId);

        Assert.Null(await ctx.Manifests.GetAsync(manifest.BackupId));
    }

    [Fact]
    public async Task AnUnreadableManifest_StillOwnsItsBackupId_AndIsReportedAsCorrupt()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        BackupManifest good = BackupManifest.CreateFull([]);
        await ctx.Manifests.PutAsync(good);

        Guid corruptId = Guid.NewGuid();
        await ctx.PutRawAsync(ctx.Layout.ManifestKey(corruptId), Encoding.UTF8.GetBytes("{ this is not json"));

        // The id set is derived from names alone, so a manifest nobody can parse still protects its
        // artifacts from the orphan sweep. This is the invariant that stops a corrupt manifest from
        // turning into deleted backup data.
        IReadOnlyList<Guid> owned = await ctx.Manifests.ListManifestIdsAsync();
        Assert.Contains(good.BackupId, owned);
        Assert.Contains(corruptId, owned);

        // It is absent from the valid listing but reported, rather than silently dropped from both.
        IReadOnlyList<BackupManifest> valid = await ctx.Manifests.ListAsync();
        Assert.Equal([good.BackupId], valid.Select(m => m.BackupId).ToArray());

        IReadOnlyList<(Guid backupId, string reason)> corrupt = await ctx.Manifests.ListCorruptAsync();
        Assert.Equal(corruptId, Assert.Single(corrupt).backupId);
    }

    [Fact]
    public async Task ArtifactKeysAreNeverMistakenForManifests()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();

        // Manifests and artifacts share one prefix, and this artifact is named to look exactly like a
        // manifest. Classifying it as one would invent a backup that has no manifest at all.
        await ctx.PutRawAsync(
            ctx.Layout.ArtifactKey(backupId, backupId.ToString("N") + ".manifest"), [1, 2, 3]);

        Assert.Empty(await ctx.Manifests.ListManifestIdsAsync());
        Assert.Empty(await ctx.Manifests.ListAsync());
    }

    [Fact]
    public async Task ManifestsUnderAnotherClustersPrefix_AreInvisible()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture, prefix: "prod/");

        GcsObjectLayout neighbour = new("staging/");
        Guid neighbourBackup = Guid.NewGuid();
        await ctx.PutRawAsync(neighbour.ManifestKey(neighbourBackup), "{}"u8.ToArray());

        BackupManifest mine = BackupManifest.CreateFull([]);
        await ctx.Manifests.PutAsync(mine);

        Assert.Equal([mine.BackupId], (await ctx.Manifests.ListManifestIdsAsync()).ToArray());
    }

    [Fact]
    public async Task ManifestListing_SeesEveryManifest_WhenThePrefixSpansManyPages()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // One key per response, so a caller that read a single response would see one manifest and
        // conclude every other backup had been deleted.
        ctx.Manifests.ListPageSize = 1;

        List<Guid> written = [];
        for (int i = 0; i < 7; i++)
        {
            BackupManifest manifest = BackupManifest.CreateFull([]);
            await ctx.Manifests.PutAsync(manifest);
            written.Add(manifest.BackupId);
        }

        IReadOnlyList<Guid> ids = await ctx.Manifests.ListManifestIdsAsync();
        Assert.Equal(written.OrderBy(id => id), ids.OrderBy(id => id));

        IReadOnlyList<BackupManifest> manifests = await ctx.Manifests.ListAsync();
        Assert.Equal(7, manifests.Count);
    }

    [Fact]
    public async Task ManifestListing_AgainstAnAbsentBucket_FailsRatherThanReportingNothing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        GcsObjectLayout layout = new("prod/");
        GcsBackupStorageTarget target = new(
            fixture.Client, "kahuna-bucket-that-does-not-exist-" + Guid.NewGuid().ToString("N")[..8], layout,
            failOnIdCollision: true);

        // An unreadable listing reported as an empty one tells the orphan sweep that no backup owns
        // anything, which reclaims every live backup in the bucket. It has to throw.
        await Assert.ThrowsAnyAsync<Exception>(() => target.ListManifestIdsAsync());
    }
}
