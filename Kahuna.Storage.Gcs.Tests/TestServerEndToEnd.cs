using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kahuna.Shared.Communication.Rest;
using Kahuna.Shared.KeyValue;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// The published extension bundle, loaded by the real Kahuna server, against the GCS emulator.
/// <para>
/// Every other test in this project calls the adapter directly. These start <c>Kahuna.Server.dll</c> with
/// <c>--extension-assembly</c> and drive it only through its REST API, so they are the tests that show the
/// bundle loads in the server, that the server uses the bucket for every backup operation, and that a
/// bucket that fails the checks stops the node.
/// </para>
/// <para>
/// The emulator models no access control, so the nodes that must work waive the two bucket protection
/// checks. The node that must not start keeps them, and the emulator bucket fails them.
/// </para>
/// </summary>
[Collection(GcsEmulatorCollection.Name)]
public class TestServerEndToEnd(GcsEmulatorFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    private readonly List<KahunaServerProcess> nodes = [];

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "kahuna-gcs-server-tests", Guid.NewGuid().ToString("N")[..12]);

    private string bucket = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (fixture.SkipReason is not null || KahunaServerProcess.SkipReason is not null)
            return;

        Directory.CreateDirectory(root);
        bucket = "kahuna-server-" + Guid.NewGuid().ToString("N")[..12];
        await fixture.Client.CreateBucketAsync(GcsEmulatorFixture.ProjectId, bucket).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (KahunaServerProcess node in nodes)
            await node.DisposeAsync().ConfigureAwait(false);

        http.Dispose();

        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not invalidate a result.
        }
    }

    [Fact]
    public async Task AStandaloneServer_BacksUpToTheBucket_AndRestoresTheChainFromIt()
    {
        SkipUnlessRunnable();

        KahunaServerProcess source = StartNode("standalone", peers: null, restoreRoot: true);
        await source.WaitUntilReadyAsync(http, StartTimeout, Ct);

        AssertLoadedTheBundle(source);

        await SetAsync(source, "e2e/before-full", "one");
        KahunaBackupInfo full = await PostBackupAsync(source, "v1/backups/full", body: null);
        Assert.Equal("Full", full.Type);

        await SetAsync(source, "e2e/after-full", "two");
        KahunaBackupInfo incremental = await PostBackupAsync(
            source, "v1/backups/incremental", new { parentBackupId = full.BackupId });
        Assert.Equal("Incremental", incremental.Type);
        Assert.Equal(full.BackupId, incremental.ParentBackupId);

        List<KahunaBackupInfo> listed = await ListAsync(source);
        Assert.Contains(listed, b => b.BackupId == full.BackupId && !b.IsInvalid);
        Assert.Contains(listed, b => b.BackupId == incremental.BackupId && !b.IsInvalid);

        // The backups live in the bucket: the manifests are objects there, and the node writes nothing
        // into its backup directory and leaves nothing in its scratch directory.
        List<string> objects = await ListBucketAsync();
        Assert.Contains(full.BackupId.ToString("N") + ".manifest", objects);
        Assert.Contains(incremental.BackupId.ToString("N") + ".manifest", objects);
        Assert.Contains(objects, key => key.StartsWith(full.BackupId.ToString("N") + "/checkpoint/", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(NodeDir(source, "backups")));
        Assert.Empty(Directory.EnumerateFiles(NodeDir(source, "scratch"), "*", SearchOption.AllDirectories));

        string target = Path.Combine(NodeDir(source, "restore"), "restored");
        KahunaRestoreResponse restored = await RestoreAsync(source, incremental.BackupId, target);
        Assert.Equal(KahunaBackupOutcome.Ok, restored.Outcome);
        Assert.Equal([full.BackupId, incremental.BackupId], restored.Chain.Select(b => b.BackupId));

        // The restored directory is a store: a new node with no backup settings serves both keys from it.
        await source.DisposeAsync();
        nodes.Remove(source);

        KahunaServerProcess fromRestore = StartPlainNode("restored", target);
        await fromRestore.WaitUntilReadyAsync(http, StartTimeout, Ct);

        Assert.Equal("one", await GetAsync(fromRestore, "e2e/before-full"));
        Assert.Equal("two", await GetAsync(fromRestore, "e2e/after-full"));
    }

    [Fact]
    public async Task AThreeNodeCluster_SharesOneCatalogInTheBucket_AndSeedsANewNodeFromIt()
    {
        SkipUnlessRunnable();

        int[] grpcPorts = [KahunaServerProcess.FreePort(), KahunaServerProcess.FreePort(), KahunaServerProcess.FreePort()];
        KahunaServerProcess[] cluster = new KahunaServerProcess[3];

        for (int i = 0; i < cluster.Length; i++)
        {
            string[] peers = grpcPorts.Where((_, j) => j != i).Select(p => $"localhost:{p}").ToArray();
            cluster[i] = StartNode($"node{i + 1}", peers, restoreRoot: false, grpcPort: grpcPorts[i], nodeId: i + 1);
        }

        foreach (KahunaServerProcess node in cluster)
            await node.WaitUntilReadyAsync(http, StartTimeout, Ct);

        foreach (KahunaServerProcess node in cluster)
            AssertLoadedTheBundle(node);

        await SetAsync(cluster[0], "e2e/before-coordinated", "one");
        (KahunaServerProcess coordinator, KahunaBackupInfo coordinated) = await TakeCoordinatedBackupAsync(cluster);
        Assert.Equal("Full", coordinated.Type);
        Assert.NotNull(coordinated.ClusterSnapshotPhysical);

        await SetAsync(cluster[0], "e2e/after-coordinated", "two");
        KahunaBackupInfo incremental = await PostBackupAsync(
            coordinator, "v1/backups/incremental", new { parentBackupId = coordinated.BackupId });
        Assert.Equal(coordinated.BackupId, incremental.ParentBackupId);

        // One bucket is one catalog: every node lists the backups, whichever node took them.
        foreach (KahunaServerProcess node in cluster)
        {
            List<KahunaBackupInfo> listed = await ListAsync(node);
            Assert.Contains(listed, b => b.BackupId == coordinated.BackupId && !b.IsInvalid);
            Assert.Contains(listed, b => b.BackupId == incremental.BackupId && !b.IsInvalid);
        }

        // A new node seeds its store from the chain in the bucket, then joins.
        string[] seeds = grpcPorts.Select(p => $"localhost:{p}").ToArray();
        KahunaServerProcess joiner = StartNode(
            "node4", seeds, restoreRoot: false, nodeId: 4,
            extra: ["--join-existing", "--pitr-bootstrap-from", incremental.BackupId.ToString()]);

        await joiner.WaitUntilReadyAsync(http, StartTimeout, Ct);

        AssertLoadedTheBundle(joiner);
        Assert.Contains($"PITR bootstrap from backup {incremental.BackupId}", joiner.Output, StringComparison.Ordinal);
        Assert.Contains("completed successfully; proceeding to join cluster", joiner.Output, StringComparison.Ordinal);

        Assert.Equal("one", await GetAsync(joiner, "e2e/before-coordinated"));
        Assert.Equal("two", await GetAsync(joiner, "e2e/after-coordinated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerWhoseBucketFailsTheChecks_DoesNotStart(bool clustered)
    {
        SkipUnlessRunnable();

        // The peers do not exist, so a clustered node that ignored the failure would wait to join and
        // never exit.
        string[]? peers = clustered
            ? [$"localhost:{KahunaServerProcess.FreePort()}", $"localhost:{KahunaServerProcess.FreePort()}"]
            : null;

        KahunaServerProcess node = StartNode(clustered ? "clustered" : "standalone", peers, restoreRoot: false, enforceBucketChecks: true);

        bool everReady = false;
        DateTime deadline = DateTime.UtcNow + StartTimeout;

        while (!node.HasExited && DateTime.UtcNow < deadline)
        {
            everReady |= await IsReadyAsync(node);
            await node.WaitForExitAsync(TimeSpan.FromMilliseconds(250), Ct);
        }

        Assert.True(node.HasExited, $"The node still runs {StartTimeout} after its start.\n{node.Output}");

        // A supervisor sees only the exit code: systemd restarts a unit with Restart=on-failure only on
        // a non-zero exit, and a Kubernetes Job counts a zero exit as success.
        Assert.True(node.ExitCode == 1, $"The node exited with code {node.ExitCode}, not 1.\n{node.Output}");
        Assert.False(everReady, $"The node reported ready although its bucket fails the checks.\n{node.Output}");
        Assert.Contains("does not have uniform bucket-level access enabled", node.Output, StringComparison.Ordinal);
        Assert.Empty(await ListBucketAsync());
    }

    private void SkipUnlessRunnable()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        Assert.SkipWhen(KahunaServerProcess.SkipReason is not null, KahunaServerProcess.SkipReason ?? "");
    }

    private KahunaServerProcess StartNode(
        string name,
        string[]? peers,
        bool restoreRoot,
        int? grpcPort = null,
        int nodeId = 1,
        bool enforceBucketChecks = false,
        string[]? extra = null)
    {
        string dir = CreateNodeDir(name);
        int httpPort = KahunaServerProcess.FreePort();
        int grpc = grpcPort ?? KahunaServerProcess.FreePort();

        List<string> args = NodeArguments(name, nodeId, dir, httpPort, grpc, peers);

        args.AddRange(
        [
            "--extension-assembly", KahunaServerProcess.BundleDll!,
            "--pitr-backup-dir", Path.Combine(dir, "backups"),
            "--pitr-backup-target", GcsBackupStorageProvider.Name,
            "--pitr-backup-scratch-dir", Path.Combine(dir, "scratch")
        ]);

        if (restoreRoot)
            args.AddRange(["--pitr-restore-root", Path.Combine(dir, "restore")]);

        args.Add("--extension-option");
        args.Add($"{GcsExtensionOptions.Bucket}={bucket}");
        args.Add($"{GcsExtensionOptions.EmulatorBaseUri}={fixture.Endpoint}");

        if (!enforceBucketChecks)
        {
            args.Add($"{GcsExtensionOptions.RequireUniformBucketLevelAccess}=false");
            args.Add($"{GcsExtensionOptions.RequirePublicAccessPrevention}=false");
        }

        if (extra is not null)
            args.AddRange(extra);

        return Track(KahunaServerProcess.Start(name, dir, httpPort, grpc, args));
    }

    /// <summary>A node with no backup settings, over an existing store.</summary>
    private KahunaServerProcess StartPlainNode(string name, string storagePath)
    {
        string dir = CreateNodeDir(name);
        int httpPort = KahunaServerProcess.FreePort();
        int grpc = KahunaServerProcess.FreePort();

        List<string> args = NodeArguments(name, 1, dir, httpPort, grpc, peers: null);
        int storage = args.IndexOf("--storage-path");
        args[storage + 1] = storagePath;

        return Track(KahunaServerProcess.Start(name, dir, httpPort, grpc, args));
    }

    private static List<string> NodeArguments(string name, int nodeId, string dir, int httpPort, int grpcPort, string[]? peers)
    {
        List<string> args =
        [
            "--raft-nodename", name,
            "--raft-nodeid", nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--raft-host", "localhost",
            "--http-ports", httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--grpc-cleartext-ports", grpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--allow-plaintext-listener",
            "--storage", "rocksdb",
            "--storage-path", Path.Combine(dir, "data"),
            "--wal-storage", "rocksdb",
            "--wal-path", Path.Combine(dir, "wal")
        ];

        if (peers is not null)
        {
            // Raft runs over the cleartext gRPC listener, so the cluster needs no certificate.
            args.AddRange(
            [
                "--raft-port", grpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--raft-grpc-scheme", "http://",
                "--initial-cluster-partitions", "3",
                "--initial-cluster"
            ]);
            args.AddRange(peers);
        }

        return args;
    }

    private string CreateNodeDir(string name)
    {
        string dir = Path.Combine(root, name);
        foreach (string sub in new[] { "data", "wal", "backups", "scratch", "restore" })
            Directory.CreateDirectory(Path.Combine(dir, sub));

        return dir;
    }

    private string NodeDir(KahunaServerProcess node, string sub) => Path.Combine(root, node.Name, sub);

    private KahunaServerProcess Track(KahunaServerProcess node)
    {
        nodes.Add(node);
        return node;
    }

    private static void AssertLoadedTheBundle(KahunaServerProcess node)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(KahunaServerProcess.BundleDll!)));

        Assert.Contains($"(sha256 {sha256})", node.Output, StringComparison.Ordinal);
        Assert.Contains($"[backup:{GcsBackupStorageProvider.Name}]", node.Output, StringComparison.Ordinal);
    }

    private async Task<bool> IsReadyAsync(KahunaServerProcess node)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(node.BaseAddress, "v1/cluster/health"), Ct);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task SetAsync(KahunaServerProcess node, string key, string value)
    {
        await RetryWhileAsync(async () =>
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                new Uri(node.BaseAddress, "v1/kv/try-set"),
                new { key, value = Encoding.UTF8.GetBytes(value), durability = KeyValueDurability.Persistent },
                Ct);

            response.EnsureSuccessStatusCode();
            KeyValueResponseType type = await ReadTypeAsync(response);

            if (type == KeyValueResponseType.MustRetry)
                return false;

            Assert.Equal(KeyValueResponseType.Set, type);
            return true;
        }, $"set {key} on {node}");
    }

    /// <summary>
    /// Reads a key. A node answers MustRetry for a short time after its start, while it settles the
    /// route of each partition, so that answer is retried up to the operation timeout.
    /// </summary>
    private async Task<string?> GetAsync(KahunaServerProcess node, string key)
    {
        string? value = null;

        await RetryWhileAsync(async () =>
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                new Uri(node.BaseAddress, "v1/kv/try-get"),
                new { key, durability = KeyValueDurability.Persistent },
                Ct);

            response.EnsureSuccessStatusCode();
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            KeyValueResponseType type = (KeyValueResponseType)body.RootElement.GetProperty("type").GetInt32();

            if (type == KeyValueResponseType.MustRetry)
                return false;

            Assert.Equal(KeyValueResponseType.Get, type);
            value = Encoding.UTF8.GetString(body.RootElement.GetProperty("value").GetBytesFromBase64());
            return true;
        }, $"get {key} on {node}");

        return value;
    }

    /// <summary>
    /// The outcomes for which the backup contract says that nothing was published and the caller may
    /// retry. A node answers some of them for a short time after the cluster forms: the meta leader can
    /// move, and a transaction of the cluster's own start can be in flight during the first capture.
    /// </summary>
    private static readonly HashSet<string> RetryableBackupOutcomes = new(StringComparer.Ordinal)
    {
        nameof(KahunaBackupOutcome.RetryableLeadershipLoss),
        nameof(KahunaBackupOutcome.TopologyChanged),
        nameof(KahunaBackupOutcome.NotBackupCoordinator),
        nameof(KahunaBackupOutcome.CutUnverified),
        nameof(KahunaBackupOutcome.PartitionCaptureFailed)
    };

    /// <summary>
    /// Takes a backup on one node. A retryable outcome is retried up to the operation timeout; any other
    /// refusal fails the test at once.
    /// </summary>
    private async Task<KahunaBackupInfo> PostBackupAsync(KahunaServerProcess node, string route, object? body)
    {
        DateTime deadline = DateTime.UtcNow + OperationTimeout;

        while (true)
        {
            (KahunaBackupInfo? info, string answer) = await TryPostBackupAsync(node, route, body);
            if (info is not null)
                return info;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"POST {route} on {node} did not succeed within {OperationTimeout}. Last answer: {answer}");

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }
    }

    /// <summary>
    /// Takes a coordinated backup on the node that leads the meta partition. The other nodes refuse it
    /// with NotBackupCoordinator, and the leader can move while the cluster settles, so each node is
    /// tried in turn until one accepts or the operation timeout passes.
    /// </summary>
    private async Task<(KahunaServerProcess Coordinator, KahunaBackupInfo Backup)> TakeCoordinatedBackupAsync(
        KahunaServerProcess[] cluster)
    {
        DateTime deadline = DateTime.UtcNow + OperationTimeout;
        string last = "";

        while (DateTime.UtcNow < deadline)
        {
            foreach (KahunaServerProcess node in cluster)
            {
                (KahunaBackupInfo? info, string answer) = await TryPostBackupAsync(node, "v1/backups/coordinated", body: null);
                if (info is not null)
                    return (node, info);

                last = answer;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        throw new TimeoutException($"No node took the coordinated backup within {OperationTimeout}. Last answer: {last}");
    }

    /// <summary>
    /// One backup request. Returns the backup on success, or null with the answer when the outcome is
    /// retryable. Fails the test on any other answer.
    /// </summary>
    private async Task<(KahunaBackupInfo? Info, string Answer)> TryPostBackupAsync(
        KahunaServerProcess node, string route, object? body)
    {
        using HttpResponseMessage response = body is null
            ? await http.PostAsync(new Uri(node.BaseAddress, route), content: null, Ct)
            : await http.PostAsJsonAsync(new Uri(node.BaseAddress, route), body, Ct);

        string text = await response.Content.ReadAsStringAsync(Ct);
        string answer = $"POST {route} on {node} answered {(int)response.StatusCode}: {text}";

        if (response.IsSuccessStatusCode)
        {
            KahunaBackupInfo? info = JsonSerializer.Deserialize<KahunaBackupInfo>(text);
            Assert.NotNull(info);
            Assert.False(info.IsInvalid, info.InvalidReason);
            return (info, answer);
        }

        bool retryable = response.Headers.TryGetValues(KahunaBackupWire.OutcomeHttpHeader, out IEnumerable<string>? outcome)
                         && outcome.Any(RetryableBackupOutcomes.Contains);
        Assert.True(retryable, answer);
        return (null, answer);
    }

    private async Task<List<KahunaBackupInfo>> ListAsync(KahunaServerProcess node)
    {
        List<KahunaBackupInfo>? listed =
            await http.GetFromJsonAsync<List<KahunaBackupInfo>>(new Uri(node.BaseAddress, "v1/backups"), Ct);

        Assert.NotNull(listed);
        return listed;
    }

    private async Task<KahunaRestoreResponse> RestoreAsync(KahunaServerProcess node, Guid leaf, string targetDir)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            new Uri(node.BaseAddress, "v1/restore"),
            new { leafBackupId = leaf, targetDir, targetTimeMs = 0L },
            Ct);

        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"POST v1/restore on {node} answered {(int)response.StatusCode}: {text}");

        KahunaRestoreResponse? restored = JsonSerializer.Deserialize<KahunaRestoreResponse>(text);
        Assert.NotNull(restored);
        return restored;
    }

    private async Task<List<string>> ListBucketAsync()
    {
        List<string> keys = [];
        await foreach (Google.Apis.Storage.v1.Data.Object item in fixture.Client.ListObjectsAsync(bucket, ""))
            keys.Add(item.Name);

        return keys;
    }

    private static async Task<KeyValueResponseType> ReadTypeAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return (KeyValueResponseType)body.RootElement.GetProperty("type").GetInt32();
    }

    private static async Task RetryWhileAsync(Func<Task<bool>> attempt, string what)
    {
        DateTime deadline = DateTime.UtcNow + OperationTimeout;

        while (!await attempt())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Could not {what} within {OperationTimeout}: the node kept answering MustRetry.");

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }
}
