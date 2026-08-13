using System.Net;
using Google.Cloud.Storage.V1;
using Kahuna.Server.Persistence.Pitr;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// The two protocol behaviours the local emulator cannot exercise: following listing page tokens, and
/// publishing a manifest under a create-if-absent precondition. Both are asserted against a scripted
/// transport, so what is verified is the request the adapter sends and what it makes of the response —
/// see <see cref="StubTransport"/> for why the emulator cannot stand in here.
/// </summary>
public class TestProtocolBehavior
{
    private const string Bucket = "kahuna-protocol-tests";

    // ── listing pagination ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArtifactListing_FollowsPageTokens_UntilTheLastPage()
    {
        Guid backupId = Guid.NewGuid();
        GcsObjectLayout layout = new("prod/");
        string prefix = layout.ArtifactPrefix(backupId);

        StubTransport transport = new(request =>
        {
            string? token = QueryValue(request, "pageToken");

            return token switch
            {
                null => StubTransport.Json(HttpStatusCode.OK,
                    StubListing.Page([prefix + "checkpoint/a.sst", prefix + "checkpoint/b.sst"], "page-2")),
                "page-2" => StubTransport.Json(HttpStatusCode.OK,
                    StubListing.Page([prefix + "checkpoint/c.sst", prefix + "checkpoint/d.sst"], "page-3")),
                "page-3" => StubTransport.Json(HttpStatusCode.OK,
                    StubListing.Page([prefix + "partition_0.wal"], null)),
                _ => StubTransport.Json(HttpStatusCode.InternalServerError, """{"error":{"code":500}}""")
            };
        });

        GcsBackupArtifactStore store = CreateArtifactStore(transport, layout);

        IReadOnlyList<BackupArtifactEntry> entries = await store.ListAsync(backupId);

        // A caller that stopped at the first response would see two artifacts and verify a backup that
        // is missing three of them, which is a verification pass reporting success over a partial image.
        Assert.Equal(
            ["checkpoint/a.sst", "checkpoint/b.sst", "checkpoint/c.sst", "checkpoint/d.sst", "partition_0.wal"],
            entries.Select(e => e.RelativePath).ToArray());

        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal(["page-2", "page-3"],
            transport.Requests.Skip(1).Select(r => QueryValue(r, "pageToken") ?? "").ToArray());
    }

    [Fact]
    public async Task BackupIdListing_FollowsPageTokens_SoNoBackupIsMissedBySweeps()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        GcsObjectLayout layout = new("prod/");

        StubTransport transport = new(request => QueryValue(request, "pageToken") switch
        {
            null => StubTransport.Json(HttpStatusCode.OK,
                StubListing.Page([layout.ArtifactKey(first, "partition_0.wal")], "page-2")),
            _ => StubTransport.Json(HttpStatusCode.OK,
                StubListing.Page([layout.ArtifactKey(second, "partition_0.wal")], null))
        });

        GcsBackupArtifactStore store = CreateArtifactStore(transport, layout);

        IReadOnlyList<Guid> ids = await store.ListBackupIdsAsync();

        // This set is the orphan sweep's candidate list. Missing a page means a backup's artifacts are
        // never considered for reclamation and sit in the bucket forever.
        Assert.Equal(new[] { first, second }.OrderBy(id => id).ToArray(), ids.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task PrefixDelete_DeletesEveryKeyAcrossEveryPage()
    {
        Guid backupId = Guid.NewGuid();
        GcsObjectLayout layout = new("prod/");
        string prefix = layout.ArtifactPrefix(backupId);

        List<string> deleted = [];

        StubTransport transport = new(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleted.Add(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split("/o/")[^1]));
                return StubTransport.Json(HttpStatusCode.NoContent, "");
            }

            return QueryValue(request, "pageToken") switch
            {
                null => StubTransport.Json(HttpStatusCode.OK,
                    StubListing.Page([prefix + "checkpoint/a.sst"], "page-2")),
                _ => StubTransport.Json(HttpStatusCode.OK,
                    StubListing.Page([prefix + "partition_0.wal"], null))
            };
        });

        GcsBackupArtifactStore store = CreateArtifactStore(transport, layout);

        await store.DeleteAllAsync(backupId);

        // A short listing here leaves artifacts behind after a delete that reported success — bytes that
        // are billed forever and that no later sweep looks for, since the manifest is already gone.
        Assert.Equal([prefix + "checkpoint/a.sst", prefix + "partition_0.wal"], deleted.Order().ToArray());
    }

    [Fact]
    public async Task PrefixDelete_TreatsAnAlreadyDeletedKeyAsDone()
    {
        Guid backupId = Guid.NewGuid();
        GcsObjectLayout layout = new("prod/");
        string prefix = layout.ArtifactPrefix(backupId);

        int deleteAttempts = 0;

        StubTransport transport = new(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleteAttempts++;
                // The state a re-run finds after an earlier pass failed part-way: some keys are still
                // listed by a stale view, some are already gone.
                return StubTransport.Json(HttpStatusCode.NotFound,
                    """{"error":{"code":404,"message":"No such object."}}""");
            }

            return StubTransport.Json(HttpStatusCode.OK,
                StubListing.Page([prefix + "checkpoint/a.sst", prefix + "partition_0.wal"], null));
        });

        GcsBackupArtifactStore store = CreateArtifactStore(transport, layout);

        // Convergence, not success-exactly-once: the pass must finish the job it finds rather than
        // aborting on the first key someone else already removed.
        await store.DeleteAllAsync(backupId);

        Assert.Equal(2, deleteAttempts);
    }

    [Fact]
    public async Task PrefixDelete_StopsOnARealFailure_RatherThanReportingSuccess()
    {
        Guid backupId = Guid.NewGuid();
        GcsObjectLayout layout = new("prod/");
        string prefix = layout.ArtifactPrefix(backupId);

        StubTransport transport = new(request =>
        {
            if (request.Method == HttpMethod.Delete)
                return StubTransport.Json(HttpStatusCode.ServiceUnavailable,
                    """{"error":{"code":503,"message":"Backend error."}}""");

            return StubTransport.Json(HttpStatusCode.OK,
                StubListing.Page([prefix + "checkpoint/a.sst"], null));
        });

        GcsBackupArtifactStore store = CreateArtifactStore(transport, layout);

        // Reclamation reports the bytes it freed. Swallowing a delete failure would report bytes that
        // are still there and still billed.
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAllAsync(backupId));
    }

    // ── manifest publish preconditions ────────────────────────────────────────────────────────

    [Fact]
    public async Task ManifestPublish_CarriesTheCreateIfAbsentPrecondition()
    {
        StubTransport transport = new(request =>
            StubTransport.TryOpenUploadSession(request) ?? StubTransport.UploadedObject(request, "manifest"));

        GcsObjectLayout layout = new("prod/");
        GcsBackupStorageTarget target = new(transport.CreateClient(), Bucket, layout, failOnIdCollision: true);

        await PublishIgnoringChecksumValidation(target);

        // The precondition is what makes a repeated backup id fail loudly instead of destroying the
        // record of the backup that already holds it. The local emulator accepts the write either way,
        // so the request is the only place this is observable.
        Assert.Contains(transport.Requests, r => QueryValue(r, "ifGenerationMatch") == "0");
    }

    [Fact]
    public async Task ManifestPublish_OmitsThePrecondition_WhenCollisionCheckingIsOff()
    {
        StubTransport transport = new(request =>
            StubTransport.TryOpenUploadSession(request) ?? StubTransport.UploadedObject(request, "manifest"));

        GcsObjectLayout layout = new("prod/");
        GcsBackupStorageTarget target = new(transport.CreateClient(), Bucket, layout, failOnIdCollision: false);

        await PublishIgnoringChecksumValidation(target);

        Assert.DoesNotContain(transport.Requests, r => QueryValue(r, "ifGenerationMatch") is not null);
    }

    [Fact]
    public async Task ManifestPublish_TranslatesAFailedPrecondition_IntoARefusalToOverwrite()
    {
        // The service rejects the write when the session is opened, which is where the precondition is
        // evaluated — so the refusal arrives before a single byte of the manifest is sent.
        StubTransport transport = new(_ => StubTransport.Json(HttpStatusCode.PreconditionFailed,
            """{"error":{"code":412,"message":"At least one of the pre-conditions you specified did not hold."}}"""));

        GcsObjectLayout layout = new("prod/");
        GcsBackupStorageTarget target = new(transport.CreateClient(), Bucket, layout, failOnIdCollision: true);

        BackupArtifactException ex = await Assert.ThrowsAsync<BackupArtifactException>(
            () => target.PutAsync(BackupManifest.CreateFull([])));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestListing_PropagatesAFailure_InsteadOfReportingNoBackups()
    {
        StubTransport transport = new(_ => StubTransport.Json(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":503,"message":"Backend error."}}"""));

        GcsObjectLayout layout = new("prod/");
        GcsBackupStorageTarget target = new(transport.CreateClient(), Bucket, layout, failOnIdCollision: true);

        // "No manifests" means "no backup owns anything", which authorises the sweep to reclaim every
        // artifact in the bucket. A transient outage must never be able to say it.
        await Assert.ThrowsAnyAsync<Exception>(() => target.ListManifestIdsAsync());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes a manifest and tolerates the one failure a scripted service cannot avoid: the client
    /// validates a finished upload against checksums only the real service can compute, so the write
    /// never completes here. Nothing else is swallowed — a different failure still fails the test —
    /// and what these tests assert is the request the adapter issued, which is fully formed by then.
    /// </summary>
    private static async Task PublishIgnoringChecksumValidation(GcsBackupStorageTarget target)
    {
        try
        {
            await target.PutAsync(BackupManifest.CreateFull([]));
        }
        catch (UploadValidationException)
        {
        }
    }

    private static GcsBackupArtifactStore CreateArtifactStore(StubTransport transport, GcsObjectLayout layout) =>
        new(transport.CreateClient(), Bucket, layout, Path.GetTempPath(),
            uploadThresholdBytes: 1024, uploadChunkSizeBytes: 256 * 1024, NullLogger.Instance);

    private static string? QueryValue(HttpRequestMessage request, string name)
    {
        string query = request.RequestUri?.Query ?? "";
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts[0] == name)
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        return null;
    }
}
