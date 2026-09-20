using Xunit;

namespace SteamService.Tests;

/// <summary>
/// The pruned content manifest never matches the depot by size or hash, so the validation pass
/// accepts it only in the exact state the prune leaves; anything else is re-fetched.
/// </summary>
public class ContentManifestTests : IDisposable
{
    private readonly string _contentDir = Path.Combine(
        Path.GetTempPath(),
        $"sdvd-manifest-{Guid.NewGuid():N}"
    );
    private string ManifestPath => Path.Combine(_contentDir, "ContentHashes.json");

    public ContentManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(_contentDir, "Characters"));
        File.WriteAllText(Path.Combine(_contentDir, "Characters", "Vincent.xnb"), "x");
        File.WriteAllText(Path.Combine(_contentDir, "Data.xnb"), "x");
    }

    public void Dispose() => Directory.Delete(_contentDir, recursive: true);

    [Fact]
    public void PrunedManifestWithEveryAssetPresentIsAccepted()
    {
        File.WriteAllText(ManifestPath, """{"Characters/Vincent.xnb":"h1","Data.xnb":"h2"}""");

        Assert.True(SteamAuthService.IsPrunedContentManifest(ManifestPath));
    }

    [Fact]
    public void EntryForAMissingAssetIsRejected()
    {
        File.WriteAllText(ManifestPath, """{"Characters/Vincent.xnb":"h1","Gone.xnb":"h3"}""");

        Assert.False(SteamAuthService.IsPrunedContentManifest(ManifestPath));
    }

    [Theory]
    [InlineData("StardewValley", true)] // SMAPI's launcher replaces it
    [InlineData("steam_appid.txt", true)] // startapp.sh writes it every boot
    [InlineData("StardewValley-original", false)]
    [InlineData("Content/Characters/Vincent.xnb", false)]
    public void PostInstallOwnedFiles_AreExactlyTheKnownRewrites(string depotFile, bool expected)
    {
        Assert.Equal(expected, SteamAuthService.IsPostInstallOwned(depotFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"Characters/Vincent.xnb":"h1",""")] // truncated
    [InlineData("\0\0\0\0garbage")]
    public void UnparseableOrEmptyManifestIsRejected(string body)
    {
        File.WriteAllText(ManifestPath, body);

        Assert.False(SteamAuthService.IsPrunedContentManifest(ManifestPath));
    }
}
