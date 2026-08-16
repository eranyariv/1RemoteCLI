using OneRemoteCli.Daemon.Auth;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The agent noticing that somebody signed in or out in another process.
/// <para>
/// The tests drive <see cref="SignedInAccountWatcher.CheckAsync"/> directly rather
/// than writing files and waiting for the shell. What matters here is the decision —
/// which changes count as a change of account — and a real
/// <see cref="FileSystemWatcher"/> would add seconds of waiting to assert nothing
/// extra. That the watcher is pointed at the right file is one line of constructor.
/// </para>
/// </summary>
public sealed class SignedInAccountWatcherTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "1remote-watcher-" + Guid.NewGuid().ToString("n"));

    private SignedInAccount? _account = new("uidone.utid", "someone@example.com");
    private int _reported;
    private SignedInAccount? _lastReported;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The one that matters. MSAL rewrites the cache every time it renews an access
    /// token — roughly hourly, forever — and treating that as a credential change
    /// would drop and rebuild the hub connection every hour, interrupting whoever was
    /// watching from their phone.
    /// </summary>
    [Fact]
    public async Task IgnoresTheCacheBeingRewrittenForTheSameAccount()
    {
        using SignedInAccountWatcher watcher = Watch();
        await watcher.StartAsync();

        // A renewal: same account, brand new file.
        await watcher.CheckAsync();
        await watcher.CheckAsync();

        Assert.Equal(0, _reported);
    }

    [Fact]
    public async Task ReportsSomebodySigningOut()
    {
        using SignedInAccountWatcher watcher = Watch();
        await watcher.StartAsync();

        _account = null;
        await watcher.CheckAsync();

        Assert.Equal(1, _reported);
        Assert.Null(_lastReported);
        Assert.Null(watcher.Account);
    }

    [Fact]
    public async Task ReportsSomebodySigningInAsADifferentAccount()
    {
        using SignedInAccountWatcher watcher = Watch();
        await watcher.StartAsync();

        _account = new SignedInAccount("uidtwo.utid", "somebody-else@example.com");
        await watcher.CheckAsync();

        Assert.Equal(1, _reported);
        Assert.Equal("somebody-else@example.com", _lastReported?.Username);
    }

    /// <summary>
    /// The username is a display detail the directory is free to change; the identity
    /// is not. Reacting to a rename would drop a working connection for nothing.
    /// </summary>
    [Fact]
    public async Task IgnoresTheSameAccountUnderANewName()
    {
        using SignedInAccountWatcher watcher = Watch();
        await watcher.StartAsync();

        _account = new SignedInAccount("uidone.utid", "renamed@example.com");
        await watcher.CheckAsync();

        Assert.Equal(0, _reported);
    }

    [Fact]
    public async Task ReportsSomebodySigningInWhenNobodyWas()
    {
        _account = null;

        using SignedInAccountWatcher watcher = Watch();
        await watcher.StartAsync();

        _account = new SignedInAccount("uidone.utid", "someone@example.com");
        await watcher.CheckAsync();

        Assert.Equal(1, _reported);
    }

    /// <summary>
    /// A cache being rewritten underneath the read looks like a failure. Treating that
    /// as "signed out" would drop the connection on a passing race, so it is held
    /// rather than believed.
    /// </summary>
    [Fact]
    public async Task DoesNotTreatAFailedReadAsASignOut()
    {
        using var watcher = new SignedInAccountWatcher(
            Path.Combine(_directory, "cache.bin"),
            _ => throw new IOException("mid-write"),
            OnChanged);

        await watcher.StartAsync();
        await watcher.CheckAsync();

        Assert.Equal(0, _reported);
    }

    /// <summary>The directory does not exist until the first sign-in, and a watcher cannot be pointed at one that does not.</summary>
    [Fact]
    public void CreatesTheCacheDirectoryRatherThanRefusingToStart()
    {
        Assert.False(Directory.Exists(_directory));

        using SignedInAccountWatcher watcher = Watch();

        Assert.True(Directory.Exists(_directory));
    }

    private SignedInAccountWatcher Watch() =>
        new(Path.Combine(_directory, "cache.bin"), _ => Task.FromResult(_account), OnChanged);

    private Task OnChanged(SignedInAccount? account)
    {
        _reported++;
        _lastReported = account;

        return Task.CompletedTask;
    }
}
