using System.Security.Cryptography;
using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// The artifact half of the target, driven against a real GCS client.
/// <para>
/// The properties under test are the ones a bucket does not share with a directory: an artifact
/// becomes visible only when its write completes, a large one streams rather than being buffered,
/// reclaiming a backup is a listing plus per-object deletes that must converge when re-run, and a
/// checkpoint has to transit local disk in both directions because no backend can address a bucket.
/// </para>
/// </summary>
[Collection(GcsEmulatorCollection.Name)]
public class TestArtifactStore(GcsEmulatorFixture fixture)
{
    [Fact]
    public async Task Capabilities_DeclareWhatABucketCannotDo()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // Declaring POSIX hardening would claim a security control that is not applied anywhere.
        Assert.False(ctx.Artifacts.Capabilities.SupportsPosixHardening);
        Assert.True(ctx.Artifacts.Capabilities.SupportsCheapRangeReads);
        Assert.True(ctx.Artifacts.Capabilities.RequiresLocalScratch);
    }

    [Fact]
    public async Task AnArtifactIsInvisibleUntilItsWriteCompletes()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();

        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "partition_0.wal"))
        {
            await writer.Stream.WriteAsync("committed"u8.ToArray());

            // Mid-write the artifact must not exist. There is no rename to hide a partial object behind,
            // so if it were visible here a verifier could hash a truncated file as if it were whole.
            Assert.False(await ctx.Artifacts.ExistsAsync(backupId, "partition_0.wal"));

            await writer.CompleteAsync();
        }

        Assert.True(await ctx.Artifacts.ExistsAsync(backupId, "partition_0.wal"));
        Assert.Equal("committed"u8.ToArray(), await ReadAllAsync(ctx, backupId, "partition_0.wal"));
    }

    [Fact]
    public async Task AnAbandonedWritePublishesNothing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();

        // The shape of a WAL segment that turned out to have no committed entries, and of any write
        // an exception unwinds past: bytes were written, CompleteAsync never ran.
        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "partition_0.wal"))
        {
            await writer.Stream.WriteAsync("half a segment"u8.ToArray());
        }

        Assert.False(await ctx.Artifacts.ExistsAsync(backupId, "partition_0.wal"));
        Assert.False(await ctx.Artifacts.BackupExistsAsync(backupId));
        Assert.Empty(await ctx.ListAllKeysAsync());
    }

    [Fact]
    public async Task AnArtifactLargerThanTheThreshold_StreamsAndStillRoundTripsExactly()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        // A threshold far below the payload forces the streaming path, where the buffered prefix is
        // handed to the upload and the rest follows. Getting that handover wrong corrupts the artifact
        // in a way only a byte comparison catches — a truncated or double-written prefix still uploads
        // cleanly and still lists with a plausible size.
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(
            fixture, uploadThresholdBytes: 4096, uploadChunkSizeBytes: 256 * 1024);

        Guid backupId = Guid.NewGuid();
        byte[] payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "checkpoint/big.sst"))
        {
            // Written in pieces, so the threshold is crossed part-way through rather than on the first call.
            for (int offset = 0; offset < payload.Length; offset += 64 * 1024)
                await writer.Stream.WriteAsync(payload.AsMemory(offset, Math.Min(64 * 1024, payload.Length - offset)));

            await writer.CompleteAsync();
        }

        Assert.Equal(payload, await ReadAllAsync(ctx, backupId, "checkpoint/big.sst"));

        BackupArtifactEntry entry = Assert.Single(await ctx.Artifacts.ListAsync(backupId));
        Assert.Equal("checkpoint/big.sst", entry.RelativePath);
        Assert.Equal(payload.Length, entry.Length);
    }

    [Fact]
    public async Task AnAbandonedStreamingWritePublishesNothing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(
            fixture, uploadThresholdBytes: 1024, uploadChunkSizeBytes: 256 * 1024);

        Guid backupId = Guid.NewGuid();

        // Past the threshold an upload is already in flight when the write is abandoned, which is the
        // case that could actually leave something behind if the session were finished instead of torn
        // down.
        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "checkpoint/big.sst"))
        {
            await writer.Stream.WriteAsync(RandomNumberGenerator.GetBytes(512 * 1024));
        }

        Assert.False(await ctx.Artifacts.ExistsAsync(backupId, "checkpoint/big.sst"));
        Assert.Empty(await ctx.ListAllKeysAsync());
    }

    [Theory]
    [InlineData(64 * 1024)]   // stays buffered
    [InlineData(1024)]        // crosses the threshold and streams
    public async Task AHashingWriterDrivingTheStreamSynchronously_ProducesTheExactBytes(int thresholdBytes)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(
            fixture, uploadThresholdBytes: thresholdBytes, uploadChunkSizeBytes: 256 * 1024);

        Guid backupId = Guid.NewGuid();
        byte[] payload = RandomNumberGenerator.GetBytes(48 * 1024);

        // This is the shape the WAL segment writer uses: the artifact stream is wrapped in a hashing
        // stream, and closing that wrapper flushes its final block *synchronously*. A stream that only
        // implemented the async path would throw there, or worse, silently drop the tail.
        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "partition_0.wal"))
        {
            using (SHA256 sha = SHA256.Create())
            {
                await using (CryptoStream hashing = new(writer.Stream, sha, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await hashing.WriteAsync(payload);
                    hashing.FlushFinalBlock();
                }
            }

            await writer.CompleteAsync();
        }

        Assert.Equal(payload, await ReadAllAsync(ctx, backupId, "partition_0.wal"));
    }

    [Fact]
    public async Task CompletingAWriteTwice_IsRefused()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();

        await using IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "partition_0.wal");
        await writer.Stream.WriteAsync("x"u8.ToArray());
        await writer.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync());
    }

    [Fact]
    public async Task ARangedRead_ReturnsExactlyThatSlice()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        byte[] payload = "0123456789abcdef"u8.ToArray();
        await WriteArtifactAsync(ctx, backupId, "partition_0.wal", payload);

        await using (Stream slice = await ctx.Artifacts.OpenReadAsync(backupId, "partition_0.wal", offset: 4, length: 6))
        {
            Assert.Equal("456789"u8.ToArray(), await ReadToEndAsync(slice));
        }

        // An offset with no length means "from here to the end", which becomes an open-ended HTTP range
        // rather than a range ending at a byte nobody computed.
        await using (Stream tail = await ctx.Artifacts.OpenReadAsync(backupId, "partition_0.wal", offset: 10))
        {
            Assert.Equal("abcdef"u8.ToArray(), await ReadToEndAsync(tail));
        }
    }

    [Fact]
    public async Task ReadingAnArtifactThatIsNotThere_ReportsItRatherThanReturningNothing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // A missing object surfacing as an empty stream would be hashed as a valid empty artifact, so
        // the absence has to be raised before any byte is read.
        await Assert.ThrowsAsync<BackupArtifactException>(
            () => ctx.Artifacts.OpenReadAsync(Guid.NewGuid(), "partition_0.wal"));
    }

    [Fact]
    public async Task ArtifactListing_SeesEveryArtifact_WhenTheBackupSpansManyPages()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        for (int i = 0; i < 7; i++)
            await WriteArtifactAsync(ctx, backupId, $"checkpoint/{i:D6}.sst", [(byte)i]);

        // A checkpoint tree routinely exceeds one response. A short listing here verifies a partial
        // image and reports it as complete.
        ctx.Artifacts.ListPageSize = 2;

        IReadOnlyList<BackupArtifactEntry> entries = await ctx.Artifacts.ListAsync(backupId);

        Assert.Equal(7, entries.Count);
        Assert.Equal(entries.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.Ordinal),
            entries.Select(e => e.RelativePath));
    }

    [Fact]
    public async Task ListingArtifacts_IgnoresOtherBackupsAndManifests()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid mine = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        await WriteArtifactAsync(ctx, mine, "partition_0.wal", [1]);
        await WriteArtifactAsync(ctx, other, "partition_0.wal", [2]);
        await ctx.Manifests.PutAsync(BackupManifest.CreateFull([]));

        BackupArtifactEntry entry = Assert.Single(await ctx.Artifacts.ListAsync(mine));
        Assert.Equal("partition_0.wal", entry.RelativePath);

        // Manifests share the prefix. One counted as an artifact would be deleted by a reclamation pass
        // over its own backup.
        IReadOnlyList<Guid> ids = await ctx.Artifacts.ListBackupIdsAsync();
        Assert.Equal(new[] { mine, other }.OrderBy(id => id).ToArray(), ids.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task DeletingABackupTwice_ConvergesToEmpty()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        await WriteArtifactAsync(ctx, backupId, "checkpoint/a.sst", [1]);
        await WriteArtifactAsync(ctx, backupId, "partition_0.wal", [2]);

        await ctx.Artifacts.DeleteAllAsync(backupId);
        Assert.False(await ctx.Artifacts.BackupExistsAsync(backupId));

        // There is no atomic prefix delete, so a pass can fail part-way and be re-run. Convergence is
        // what replaces the atomicity a directory delete had.
        await ctx.Artifacts.DeleteAllAsync(backupId);
        Assert.Empty(await ctx.ListAllKeysAsync());
    }

    [Fact]
    public async Task DeletingABackup_ConvergesFromAPartiallyDeletedPrefix()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        await WriteArtifactAsync(ctx, backupId, "checkpoint/a.sst", [1]);
        await WriteArtifactAsync(ctx, backupId, "checkpoint/b.sst", [2]);
        await WriteArtifactAsync(ctx, backupId, "partition_0.wal", [3]);

        // The state left by a delete that failed after its first object: some keys gone, some not.
        await ctx.Client.DeleteObjectAsync(ctx.Bucket, ctx.Layout.ArtifactKey(backupId, "checkpoint/a.sst"));

        await ctx.Artifacts.DeleteAllAsync(backupId);

        Assert.Empty(await ctx.ListAllKeysAsync());
    }

    [Fact]
    public async Task DeletingABackupThatWasNeverThere_Succeeds()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        await ctx.Artifacts.DeleteAllAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task ThereIsNoLeftoverDebrisToReclaim()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        await using (IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, "partition_0.wal"))
        {
            await writer.Stream.WriteAsync("abandoned"u8.ToArray());
        }

        // The local store recognises temp-file names left by an interrupted rename. Nothing equivalent
        // exists here: an unfinished upload was never an object, so reporting debris would be inventing
        // a class of key that does not occur.
        Assert.Empty(await ctx.Artifacts.ListLeftoversAsync());
    }

    // ── staging and materialisation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ACheckpointStagesLocally_ThenUploadsUnderTheCheckpointPrefix()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        string stagedPath;

        await using (IBackupCheckpointStagingArea staging = await ctx.Artifacts.BeginCheckpointAsync(backupId))
        {
            stagedPath = staging.LocalPath;

            // The parent exists and the leaf does not: a backend publishes its checkpoint by renaming a
            // sibling temp tree onto this path, and that rename fails if anything is already there.
            Assert.True(Directory.Exists(Path.GetDirectoryName(stagedPath)));
            Assert.False(Directory.Exists(stagedPath));

            Directory.CreateDirectory(Path.Combine(stagedPath, "42"));
            await File.WriteAllBytesAsync(Path.Combine(stagedPath, "CURRENT"), "rev"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(stagedPath, "42", "000012.sst"), "data"u8.ToArray());

            await staging.CommitAsync();
        }

        Assert.Equal(
            ["checkpoint/42/000012.sst", "checkpoint/CURRENT"],
            (await ctx.Artifacts.ListAsync(backupId)).Select(e => e.RelativePath).ToArray());

        // Scratch is transient. A surviving tree would either break the next backup's publishing rename
        // or, worse, look like valid staged input to it.
        Assert.False(Directory.Exists(stagedPath));
    }

    [Fact]
    public async Task AnAbandonedCheckpointUploadsNothingAndLeavesNoScratch()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        string stagedPath;

        await using (IBackupCheckpointStagingArea staging = await ctx.Artifacts.BeginCheckpointAsync(backupId))
        {
            stagedPath = staging.LocalPath;
            Directory.CreateDirectory(stagedPath);
            await File.WriteAllBytesAsync(Path.Combine(stagedPath, "CURRENT"), "rev"u8.ToArray());
        }

        // No commit, so the manifest would never have been published either — the artifacts-then-manifest
        // ordering means nothing resolves to a backup whose checkpoint never landed.
        Assert.Empty(await ctx.ListAllKeysAsync());
        Assert.False(Directory.Exists(stagedPath));
    }

    [Fact]
    public async Task MaterialisingACheckpointPreservesItsLayoutExactly()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        await WriteArtifactAsync(ctx, backupId, "checkpoint/CURRENT", "rev"u8.ToArray());
        await WriteArtifactAsync(ctx, backupId, "checkpoint/42/000012.sst", "data"u8.ToArray());
        await WriteArtifactAsync(ctx, backupId, "partition_0.wal", "wal"u8.ToArray());

        string destination = Path.Combine(ctx.ScratchRoot, "restore-staging");

        await ctx.Artifacts.MaterializeAsync(backupId, "checkpoint", destination);

        // A backend opens this tree and is layout-sensitive — RocksDB expects {dir}/{revision}/, SQLite
        // expects files directly in {dir}/ — so flattening or reordering keys produces a directory that
        // looks plausible and cannot be opened.
        Assert.Equal("rev"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(destination, "CURRENT")));
        Assert.Equal("data"u8.ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(destination, "42", "000012.sst")));

        // Only the requested prefix is materialised; the WAL segment sits outside it.
        Assert.False(File.Exists(Path.Combine(destination, "partition_0.wal")));
    }

    [Fact]
    public async Task MaterialisingAPrefixThatIsNotThere_Fails()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        // Silently producing an empty directory would let a backend be opened on nothing at all.
        await Assert.ThrowsAsync<BackupArtifactException>(() => ctx.Artifacts.MaterializeAsync(
            Guid.NewGuid(), "checkpoint", Path.Combine(ctx.ScratchRoot, "restore-staging")));
    }

    [Fact]
    public async Task MaterialisingWithAThrottle_StillTransfersEveryByte()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using GcsTestContext ctx = await GcsTestContext.CreateAsync(fixture);

        Guid backupId = Guid.NewGuid();
        byte[] payload = RandomNumberGenerator.GetBytes(256 * 1024);
        await WriteArtifactAsync(ctx, backupId, "checkpoint/000012.sst", payload);

        string destination = Path.Combine(ctx.ScratchRoot, "throttled");

        // The throttled path is a different transfer loop from the unthrottled one, so it gets its own
        // byte-for-byte check rather than being assumed equivalent.
        await ctx.Artifacts.MaterializeAsync(
            backupId, "checkpoint", destination, throttleBytesPerSec: 8 * 1024 * 1024);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "000012.sst")));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static async Task WriteArtifactAsync(
        GcsTestContext ctx, Guid backupId, string relativePath, byte[] content)
    {
        await using IBackupArtifactWriter writer = await ctx.Artifacts.OpenWriteAsync(backupId, relativePath);
        await writer.Stream.WriteAsync(content);
        await writer.CompleteAsync();
    }

    private static async Task<byte[]> ReadAllAsync(GcsTestContext ctx, Guid backupId, string relativePath)
    {
        await using Stream stream = await ctx.Artifacts.OpenReadAsync(backupId, relativePath);
        return await ReadToEndAsync(stream);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
