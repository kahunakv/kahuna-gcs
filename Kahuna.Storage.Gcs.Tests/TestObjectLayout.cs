using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// Manifests and artifacts share one bucket prefix, exactly as they share one directory on the local
/// target, so the only thing separating them is how a key's name is read. Getting that wrong is
/// destructive in both directions — a manifest classified as an artifact is reclaimed by the sweep, an
/// artifact classified as a manifest hides bytes that should have been freed — which is why the
/// classification is pinned down here rather than left to the integration tests to notice.
/// </summary>
public class TestObjectLayout
{
    private static readonly Guid Id = Guid.Parse("11112222-3333-4444-5555-666677778888");

    [Theory]
    [InlineData("", "11112222333344445555666677778888.manifest")]
    [InlineData("prod/", "prod/11112222333344445555666677778888.manifest")]
    [InlineData("prod", "prod/11112222333344445555666677778888.manifest")]
    [InlineData("/prod/", "prod/11112222333344445555666677778888.manifest")]
    [InlineData("  a/b  ", "a/b/11112222333344445555666677778888.manifest")]
    public void ManifestKey_NormalizesThePrefix(string prefix, string expected)
    {
        GcsObjectLayout layout = new(prefix);
        Assert.Equal(expected, layout.ManifestKey(Id));
    }

    [Fact]
    public void ArtifactKey_PlacesArtifactsUnderTheBackupPrefix()
    {
        GcsObjectLayout layout = new("prod/");

        Assert.Equal(
            "prod/11112222333344445555666677778888/checkpoint/000012.sst",
            layout.ArtifactKey(Id, "checkpoint/000012.sst"));
    }

    [Fact]
    public void ArtifactKey_AcceptsWindowsSeparators_SoBothHostsProduceTheSameKey()
    {
        GcsObjectLayout layout = new("");

        Assert.Equal(
            "11112222333344445555666677778888/checkpoint/000012.sst",
            layout.ArtifactKey(Id, @"checkpoint\000012.sst"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/system32")]
    [InlineData("../11112222333344445555666677778888/checkpoint/x")]
    [InlineData("checkpoint/../../other/x")]
    [InlineData("checkpoint//x")]
    [InlineData("./x")]
    public void ArtifactKey_RefusesAPathThatDoesNotAddressThisBackup(string relativePath)
    {
        GcsObjectLayout layout = new("prod/");

        Assert.Throws<BackupArtifactException>(() => layout.ArtifactKey(Id, relativePath));
    }

    [Fact]
    public void ManifestClassification_AcceptsAManifestKeyAndRejectsAnArtifactKey()
    {
        GcsObjectLayout layout = new("prod/");

        Assert.True(layout.TryParseManifestKey(layout.ManifestKey(Id), out Guid parsed));
        Assert.Equal(Id, parsed);

        Assert.False(layout.TryParseManifestKey(layout.ArtifactKey(Id, "partition_0.wal"), out _));
    }

    [Fact]
    public void ManifestClassification_RejectsAnArtifactThatMerelyEndsInTheManifestSuffix()
    {
        // A backup whose artifact happens to be named like a manifest must not be mistaken for one:
        // the sweep would then believe a backup exists that has no manifest at all.
        GcsObjectLayout layout = new("prod/");
        string artifact = layout.ArtifactKey(Id, "checkpoint/11112222333344445555666677778888.manifest");

        Assert.False(layout.TryParseManifestKey(artifact, out _));
        Assert.True(layout.TryParseArtifactKey(artifact, out Guid owner));
        Assert.Equal(Id, owner);
    }

    [Fact]
    public void ArtifactClassification_RejectsAManifestKey()
    {
        // The reverse mistake is the destructive one: a manifest read as an artifact would be listed as
        // a backup's bytes and deleted by a reclamation pass.
        GcsObjectLayout layout = new("prod/");

        Assert.False(layout.TryParseArtifactKey(layout.ManifestKey(Id), out _));
    }

    [Fact]
    public void Classification_IgnoresKeysOutsideTheConfiguredPrefix()
    {
        // One bucket can host several clusters. A neighbour's manifest must not appear as this
        // cluster's backup, and its artifacts must not appear as this cluster's orphans.
        GcsObjectLayout layout = new("prod/");
        GcsObjectLayout other = new("staging/");

        Assert.False(layout.TryParseManifestKey(other.ManifestKey(Id), out _));
        Assert.False(layout.TryParseArtifactKey(other.ArtifactKey(Id, "partition_0.wal"), out _));
    }

    [Fact]
    public void Classification_IgnoresANameThatIsNotABackupId()
    {
        GcsObjectLayout layout = new("");

        Assert.False(layout.TryParseManifestKey("not-a-guid.manifest", out _));
        Assert.False(layout.TryParseArtifactKey("not-a-guid/checkpoint/x", out _));
    }

    [Fact]
    public void ArtifactPrefix_ForARelativePrefix_ScopesToThatSubtree()
    {
        GcsObjectLayout layout = new("prod/");

        Assert.Equal(
            "prod/11112222333344445555666677778888/checkpoint/",
            layout.ArtifactPrefix(Id, "checkpoint"));

        Assert.Equal(
            "prod/11112222333344445555666677778888/",
            layout.ArtifactPrefix(Id, ""));
    }
}
