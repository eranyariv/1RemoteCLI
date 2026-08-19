using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// What the settings window says.
/// <para>
/// The window itself is Win32 and cannot be asserted on, which is exactly why the
/// wording lives somewhere that can. Every sentence here is read by somebody whose
/// phone has just stopped seeing their machine, and the difference between a useful
/// dialog and a useless one is entirely in whether those sentences say what to do
/// next (issue #73).
/// </para>
/// </summary>
public sealed class SettingsPresenterTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static SettingsView View(
        AgentState state = AgentState.Connected,
        string? account = "ada@example.com",
        params SessionSummary[] sessions) =>
        SettingsPresenter.Present(state, account, sessions, Now);

    [Fact]
    public void SignedOutIsAValidStateAndSaysSo()
    {
        // Not an error, and it must not read like one: a machine that has never been
        // signed in is the state every new user starts in.
        SettingsView view = View(AgentState.SignedOut, account: null);

        Assert.Equal("Not signed in", view.Account);
        Assert.False(view.SignedIn);
        Assert.Contains("cannot see this machine", view.Connection, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankIsTheSameAsSignedOut()
    {
        // A whitespace description would otherwise render as "Signed in as   ".
        Assert.False(View(account: "   ").SignedIn);
    }

    [Fact]
    public void ReconnectingSaysSessionsStillWork()
    {
        // The single most important sentence in the window. Somebody looking at it has
        // a session running and needs to know it is not being lost.
        string connection = View(AgentState.Reconnecting).Connection;

        Assert.Contains("Sessions here keep working", connection, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedInWhileReconnectingStillReadsAsSignedIn()
    {
        // Keyed off the account, not the connection. A laptop on a train is
        // reconnecting, and offering to sign in an account that already is would send
        // the user round a loop that changes nothing.
        SettingsView view = View(AgentState.Reconnecting);

        Assert.True(view.SignedIn);
        Assert.Equal("Signed in as ada@example.com", view.Account);
    }

    [Fact]
    public void NoSessionsSaysHowToStartOne()
    {
        SettingsView view = View();

        Assert.False(view.HasSessions);
        Assert.Equal([SettingsPresenter.NoSessions], view.Sessions);

        // Names the command, because somebody looking at an empty list has not worked
        // out that sessions are a thing they start.
        Assert.Contains("1remote pwsh", view.Sessions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionSaysWhatItIsAndHowLongItHasBeenThere()
    {
        SettingsView view = View(
            sessions: new SessionSummary("Claude Code", Now.AddMinutes(-4), false));

        Assert.True(view.HasSessions);
        Assert.Equal("Claude Code \u2014 started 4 minutes ago", view.Sessions[0]);
    }

    [Fact]
    public void ASessionSaysWhatItIsRunningWhenWeKnow()
    {
        // The reason the setting exists: the desk should agree with the phone about
        // what is in each window, including after the user corrects a bad guess.
        string line = SettingsPresenter.Describe(
            new SessionSummary("1RemoteCLI", Now.AddMinutes(-4), false, CliType.ClaudeCode),
            Now);

        Assert.Equal("1RemoteCLI \u2014 Claude Code \u2014 started 4 minutes ago", line);
    }

    [Fact]
    public void DoesNotWriteGenericOnALineThatAlreadySaysNothing()
    {
        // "build — Generic — started 2 hours ago" spends a word telling the reader that
        // we do not know, on exactly the lines that are least worth reading.
        string line = SettingsPresenter.Describe(
            new SessionSummary("build", Now.AddHours(-2), false),
            Now);

        Assert.DoesNotContain("Generic", line, StringComparison.Ordinal);
        Assert.Equal("build \u2014 started 2 hours ago", line);
    }

    [Fact]
    public void WaitingForInputIsSaidLast()
    {
        // Last because it is the answer to "why did I open this", and the eye finds the
        // end of a line it has already started reading.
        string line = SettingsPresenter.Describe(
            new SessionSummary("build", Now.AddHours(-2), AwaitingInput: true),
            Now);

        Assert.EndsWith("\u2014 waiting for input", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionsAreOldestFirst()
    {
        // Stable ordering, so a list that refreshes once a second does not reshuffle
        // itself under someone reading it.
        SettingsView view = View(
            sessions:
            [
                new SessionSummary("newest", Now.AddMinutes(-1), false),
                new SessionSummary("oldest", Now.AddHours(-5), false),
            ]);

        Assert.StartsWith("oldest", view.Sessions[0], StringComparison.Ordinal);
        Assert.StartsWith("newest", view.Sessions[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(120, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(172800, "2 days ago")]
    public void AgeIsSaidInTheCoarsestUnitThatIsStillTrue(int seconds, string expected) =>
        Assert.Equal(expected, SettingsPresenter.Since(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AClockThatWentBackwardsDoesNotProduceANegativeAge()
    {
        // A session started microseconds ago, or a machine that resynced its clock.
        // "-1 minutes ago" is the kind of detail that makes people distrust everything
        // else in the window.
        Assert.Equal("just now", SettingsPresenter.Since(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void TheVersionIsThereBecauseEveryReportNeedsIt()
    {
        Assert.Equal($"Version {ProductVersion.Current}", View().Version);
    }
}
