using System.Runtime.InteropServices;
using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

public class ReleaseSourceTests
{
    [Fact]
    public void NamesTheAssetsTheInstallerFetches()
    {
        // scripts/install.ps1 builds "1remote-$architecture.exe" from a runtime
        // identifier, not a bare architecture. Two names for one release would be a
        // second distribution channel nobody was maintaining.
        Assert.Equal("1remote-win-x64.exe", ReleaseSource.AssetFor(Architecture.X64));
        Assert.Equal("1remote-win-arm64.exe", ReleaseSource.AssetFor(Architecture.Arm64));
        Assert.Equal("SHA256SUMS.txt", ReleaseSource.ChecksumsAsset);
    }

    /// <summary>
    /// Pinned against the workflow that publishes them, because getting these wrong does
    /// not fail loudly. A release simply appears not to list an asset for this machine,
    /// and every check for the rest of the build's life reports "there is nothing to
    /// check a download against" — which reads like a broken release rather than a typo
    /// here. That is exactly what the first draft of this class did.
    /// </summary>
    [Fact]
    public void TheNamesAreTheOnesTheReleaseWorkflowPublishes()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "release.yml"));

        Assert.Contains(ReleaseSource.AssetFor(Architecture.X64), workflow, StringComparison.Ordinal);
        Assert.Contains(ReleaseSource.AssetFor(Architecture.Arm64), workflow, StringComparison.Ordinal);
        Assert.Contains(ReleaseSource.ChecksumsAsset, workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositoryIsTheOneTheInstallerUses()
    {
        string installer = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "install.ps1"));

        Assert.Contains(ReleaseSource.Repository, installer, StringComparison.Ordinal);
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VERSION")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return directory!.FullName;
        }
    }

    /// <summary>
    /// An x64 build running under emulation must fetch x64, so the sessions it starts
    /// as copies of itself keep loading the bits the running process proved it could.
    /// </summary>
    [Fact]
    public void FallsBackToX64ForAnArchitectureThatHasNoBuild() =>
        Assert.Equal("1remote-win-x64.exe", ReleaseSource.AssetFor(Architecture.X86));

    [Fact]
    public void AsksTheWebsiteRatherThanTheApi()
    {
        // The API's sixty-anonymous-calls-an-hour is issue #102, and every agent
        // checking daily would make that routine.
        Assert.Equal("https://github.com/eranyariv/1RemoteCLI/releases/latest", ReleaseSource.LatestRelease.AbsoluteUri);
        Assert.DoesNotContain("api.github.com", ReleaseSource.LatestRelease.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsTheDownloadUrlForAnAsset() =>
        Assert.Equal(
            "https://github.com/eranyariv/1RemoteCLI/releases/download/v0.13/1remote-win-x64.exe",
            ReleaseSource.Download("v0.13", "1remote-win-x64.exe").AbsoluteUri);

    [Theory]
    [InlineData("https://github.com/eranyariv/1RemoteCLI/releases/tag/v0.13", "v0.13")]
    [InlineData("https://github.com/eranyariv/1RemoteCLI/releases/tag/v0.13/", "v0.13")]
    public void ReadsTheTagOutOfWhereItLanded(string landed, string expected) =>
        Assert.Equal(expected, ReleaseSource.TagFromRedirect(new Uri(landed)));

    /// <summary>
    /// A repository with no releases redirects nowhere, so the URL that comes back is
    /// the one that was asked for. Reading "latest" as a tag would build a download URL
    /// that 404s, and the 404 body is an HTML page with a 200 on the asset URL.
    /// </summary>
    [Fact]
    public void HasNoTagWhenNothingRedirected() =>
        Assert.Null(ReleaseSource.TagFromRedirect(ReleaseSource.LatestRelease));

    [Fact]
    public void HasNoTagWhenNothingCameBack() => Assert.Null(ReleaseSource.TagFromRedirect(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesToBuildADownloadUrlFromNothing(string value)
    {
        Assert.Throws<ArgumentException>(() => ReleaseSource.Download(value, "1remote-win-x64.exe"));
        Assert.Throws<ArgumentException>(() => ReleaseSource.Download("v0.13", value));
    }
}
