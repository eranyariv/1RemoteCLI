using System.Security.Cryptography;
using System.Text;
using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The install sequence, and every point at which it refuses.
/// <para>
/// The refusals are the feature. An installer is run by somebody watching it who can
/// re-run it; this runs by itself on a machine whose owner is elsewhere, so the bad
/// outcome is not "try again" but "the tray icon is gone and the phone cannot see this
/// machine". Every test below is a way that could happen.
/// </para>
/// </summary>
public sealed class AgentUpdateTests : IDisposable
{
    private const string Asset = "1remote-win-x64.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1remote-update-tests",
        Guid.NewGuid().ToString("n"));

    private readonly string _installed;
    private readonly string _staging;

    public AgentUpdateTests()
    {
        _installed = Path.Combine(_root, "bin", "1remote.exe");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.GetDirectoryName(_installed)!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test machine's temp directory is not the subject.
        }
    }

    private static byte[] Build(string content) => Encoding.UTF8.GetBytes(content);

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Sums(params (string Asset, byte[] Bytes)[] entries) =>
        Encoding.UTF8.GetBytes(string.Join('\n', entries.Select(e => $"{HashOf(e.Bytes)}  {e.Asset}")));

    /// <summary>Everything working: the checksums list the asset, and it is the asset.</summary>
    private UpdateSteps Working(
        byte[] program,
        string reports = "0.13",
        byte[]? checksums = null,
        List<string>? downloaded = null) =>
        new(
            (asset, _) =>
            {
                downloaded?.Add(asset);

                return Task.FromResult(asset == ReleaseSource.ChecksumsAsset
                    ? checksums ?? Sums((Asset, program))
                    : program);
            },
            _ => reports,
            AgentUpdate.ReplaceExecutable);

    private Task<UpdateResult> ApplyAsync(UpdateSteps steps, string tag = "v0.13") =>
        AgentUpdate.ApplyAsync(tag, Asset, _installed, _staging, steps);

    [Fact]
    public async Task InstallsAReleaseThatChecksOutAsync()
    {
        byte[] program = Build("the 0.13 build");
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateResult result = await ApplyAsync(Working(program));

        Assert.True(result.Ok);
        Assert.True(result.Replaced);
        Assert.Equal(program, await File.ReadAllBytesAsync(_installed));
    }

    [Fact]
    public async Task InstallsOverNothingAsync()
    {
        byte[] program = Build("the 0.13 build");

        UpdateResult result = await ApplyAsync(Working(program));

        Assert.True(result.Ok);
        Assert.Equal(program, await File.ReadAllBytesAsync(_installed));
    }

    /// <summary>
    /// Issue #108. Windows judges an executable as it is written and its verdict is not
    /// stable between two writes of identical bytes, so a pointless copy is a real risk
    /// of breaking an install that works.
    /// </summary>
    [Fact]
    public async Task WritesNothingWhenTheInstalledFileIsAlreadyThatBuildAsync()
    {
        byte[] program = Build("the 0.13 build");
        await File.WriteAllBytesAsync(_installed, program);

        DateTime before = File.GetLastWriteTimeUtc(_installed);
        List<string> downloaded = [];

        UpdateResult result = await ApplyAsync(Working(program, downloaded: downloaded));

        Assert.True(result.Ok);
        Assert.False(result.Replaced);
        Assert.Equal(File.GetLastWriteTimeUtc(_installed), before);

        // And it did not spend thirty megabytes finding that out.
        Assert.Equal([ReleaseSource.ChecksumsAsset], downloaded);
    }

    /// <summary>
    /// The checksums come first so an unverifiable release costs a few hundred bytes
    /// rather than the whole program on somebody's tethered connection.
    /// </summary>
    [Fact]
    public async Task FetchesTheChecksumsBeforeTheProgramAsync()
    {
        List<string> downloaded = [];

        await ApplyAsync(Working(Build("the 0.13 build"), downloaded: downloaded));

        Assert.Equal([ReleaseSource.ChecksumsAsset, Asset], downloaded);
    }

    [Fact]
    public async Task RefusesAReleaseThatDoesNotListTheAssetAsync()
    {
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateResult result = await ApplyAsync(
            Working(Build("the 0.13 build"), checksums: Sums(("something-else.exe", Build("x")))));

        Assert.False(result.Ok);
        Assert.Contains("checksum", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    /// <summary>
    /// A download URL GitHub cannot resolve is answered with an HTML page and a 200, so
    /// "the checksums" can arrive looking like a file.
    /// </summary>
    [Fact]
    public async Task RefusesWhenTheChecksumsAreAnHtmlPageAsync()
    {
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateResult result = await ApplyAsync(
            Working(Build("the 0.13 build"), checksums: Build("<!DOCTYPE html><html><body>Not Found</body></html>")));

        Assert.False(result.Ok);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    [Fact]
    public async Task RefusesADownloadThatDoesNotMatchItsHashAsync()
    {
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateSteps steps = new(
            (asset, _) => Task.FromResult(asset == ReleaseSource.ChecksumsAsset
                ? Sums((Asset, Build("what was published")))
                : Build("what actually arrived")),
            _ => "0.13",
            AgentUpdate.ReplaceExecutable);

        UpdateResult result = await ApplyAsync(steps);

        Assert.False(result.Ok);
        Assert.Contains("NOT been installed", result.Message, StringComparison.Ordinal);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    /// <summary>
    /// Issues #92, #93 and #101 are all "the executable arrived and then would not
    /// start", which is exactly what a machine nobody is sitting at cannot recover from.
    /// </summary>
    [Fact]
    public async Task RefusesABuildThatWillNotRunAsync()
    {
        byte[] program = Build("the 0.13 build");
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateSteps steps = Working(program) with { Prove = _ => null };

        UpdateResult result = await ApplyAsync(steps);

        Assert.False(result.Ok);
        Assert.Contains("would not run", result.Message, StringComparison.Ordinal);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    [Fact]
    public async Task RefusesABuildThatReportsADifferentVersionAsync()
    {
        byte[] program = Build("the 0.13 build");
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateResult result = await ApplyAsync(Working(program, reports: "0.11"));

        Assert.False(result.Ok);
        Assert.Contains("0.11", result.Message, StringComparison.Ordinal);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    [Fact]
    public async Task AcceptsTheVersionWrittenAsATagAsync()
    {
        UpdateResult result = await ApplyAsync(Working(Build("the 0.13 build"), reports: "v0.13"));

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task ReportsADownloadThatFailedAsync()
    {
        await File.WriteAllBytesAsync(_installed, Build("the 0.12 build"));

        UpdateSteps steps = new(
            (_, _) => throw new HttpRequestException("the network is down"),
            _ => "0.13",
            AgentUpdate.ReplaceExecutable);

        UpdateResult result = await ApplyAsync(steps);

        Assert.False(result.Ok);
        Assert.Contains("the network is down", result.Message, StringComparison.Ordinal);
        Assert.Equal(Build("the 0.12 build"), await File.ReadAllBytesAsync(_installed));
    }

    [Fact]
    public async Task ReportsAReplacementThatFailedAsync()
    {
        UpdateSteps steps = Working(Build("the 0.13 build")) with { Replace = (_, _) => "the file is in use" };

        UpdateResult result = await ApplyAsync(steps);

        Assert.False(result.Ok);
        Assert.Contains("the file is in use", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotProveABuildItAlreadyRefusedAsync()
    {
        bool proved = false;

        UpdateSteps steps = new(
            (asset, _) => Task.FromResult(asset == ReleaseSource.ChecksumsAsset
                ? Sums((Asset, Build("what was published")))
                : Build("what actually arrived")),
            _ =>
            {
                proved = true;
                return "0.13";
            },
            AgentUpdate.ReplaceExecutable);

        await ApplyAsync(steps);

        Assert.False(proved);
    }

    [Fact]
    public void ReplacingRetiresTheOldFileRatherThanDeletingIt()
    {
        // Windows will not delete a running image but will rename one, and a process
        // keeps running from the file whatever it is now called. That is the whole
        // trick that lets an agent replace itself while running, and it is why the old
        // file is still on disk afterwards rather than gone.
        string staged = Path.Combine(_staging, Asset);
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(staged, Build("the 0.13 build"));
        File.WriteAllBytes(_installed, Build("the 0.12 build"));

        string retired = _installed + ".old";

        // Held open the way a running process holds its own image: the sweep at the end
        // cannot delete it, and must not treat that as the update having failed.
        using (File.Open(retired, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Nothing to write. The handle is the point.
        }

        using FileStream held = File.Open(retired, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Null(AgentUpdate.ReplaceExecutable(staged, _installed));

        Assert.Equal(Build("the 0.13 build"), File.ReadAllBytes(_installed));
    }

    /// <summary>
    /// An agent that installed an update while sessions were open goes on running from
    /// the retired copy. A second update arriving before those sessions end must not
    /// fail on the leavings of the first — and the machine this matters most on is the
    /// one that is never idle.
    /// </summary>
    [Fact]
    public void ReplacingWorksAroundARetiredCopyThatIsStillInUse()
    {
        string staged = Path.Combine(_staging, Asset);
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(staged, Build("the 0.14 build"));
        File.WriteAllBytes(_installed, Build("the 0.13 build"));
        File.WriteAllBytes(_installed + ".old", Build("the 0.12 build, still running"));

        using FileStream held = File.Open(_installed + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Null(AgentUpdate.ReplaceExecutable(staged, _installed));

        Assert.Equal(Build("the 0.14 build"), File.ReadAllBytes(_installed));
        Assert.Equal(Build("the 0.12 build, still running"), File.ReadAllBytes(_installed + ".old"));
    }

    [Fact]
    public void ReplacingSweepsUpAnEarlierRetiredCopy()
    {
        string staged = Path.Combine(_staging, Asset);
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(staged, Build("the 0.13 build"));
        File.WriteAllBytes(_installed, Build("the 0.12 build"));
        File.WriteAllBytes(_installed + ".old", Build("the 0.11 build"));

        Assert.Null(AgentUpdate.ReplaceExecutable(staged, _installed));

        Assert.Equal(Build("the 0.13 build"), File.ReadAllBytes(_installed));

        // Nothing is running from either old copy here, so both are gone.
        Assert.False(File.Exists(_installed + ".old"));
    }

    /// <summary>
    /// The reason the old file is renamed rather than deleted: if the copy fails there
    /// is still something to put back.
    /// </summary>
    [Fact]
    public void ReplacingPutsTheOldFileBackWhenTheCopyFails()
    {
        File.WriteAllBytes(_installed, Build("the 0.12 build"));

        string? failure = AgentUpdate.ReplaceExecutable(Path.Combine(_staging, "not-there.exe"), _installed);

        Assert.NotNull(failure);
        Assert.True(File.Exists(_installed));
        Assert.Equal(Build("the 0.12 build"), File.ReadAllBytes(_installed));
        Assert.False(File.Exists(_installed + ".old"));
    }

    [Fact]
    public void ReplacingCreatesTheDestinationDirectory()
    {
        string staged = Path.Combine(_staging, Asset);
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(staged, Build("the 0.13 build"));

        string fresh = Path.Combine(_root, "somewhere", "new", "1remote.exe");

        Assert.Null(AgentUpdate.ReplaceExecutable(staged, fresh));
        Assert.Equal(Build("the 0.13 build"), File.ReadAllBytes(fresh));
    }

    [Fact]
    public async Task RefusesToBeAskedForNothingAsync()
    {
        UpdateSteps steps = Working(Build("x"));

        await Assert.ThrowsAsync<ArgumentException>(() => AgentUpdate.ApplyAsync("  ", Asset, _installed, _staging, steps));
        await Assert.ThrowsAsync<ArgumentException>(() => AgentUpdate.ApplyAsync("v0.13", "  ", _installed, _staging, steps));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AgentUpdate.ApplyAsync("v0.13", Asset, _installed, _staging, null!));
    }
}
