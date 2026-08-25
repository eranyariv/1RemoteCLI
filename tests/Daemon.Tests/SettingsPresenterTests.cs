using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Daemon.Update;
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
        Assert.Equal(StatusTone.Disabled, view.AccountTone);
        Assert.Equal(StatusTone.Disabled, view.ConnectionTone);
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
        Assert.Equal(StatusTone.Good, view.AccountTone);
        Assert.Equal(StatusTone.Connecting, view.ConnectionTone);
    }

    [Fact]
    public void ConnectedHubUsesTheHealthyStatusTone() =>
        Assert.Equal(StatusTone.Good, View().ConnectionTone);

    [Fact]
    public void NoSessionsSaysHowToStartOne()
    {
        SettingsView view = View();

        Assert.False(view.HasSessions);
        Assert.Empty(view.Sessions);

        // Names the command, because somebody looking at an empty list has not worked
        // out that sessions are a thing they start.
        Assert.Contains("1remote pwsh", SettingsPresenter.NoSessions, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionSaysWhatItIsAndHowLongItHasBeenThere()
    {
        SettingsView view = View(
            sessions: new SessionSummary("Claude Code", Now.AddMinutes(-4), false));

        Assert.True(view.HasSessions);
        Assert.Equal("Claude Code", view.Sessions[0].Name);
        Assert.Equal("Started 4 minutes ago", view.Sessions[0].Activity);
    }

    [Theory]
    [InlineData(0, "0 sessions discovered on this machine")]
    [InlineData(1, "1 session discovered on this machine")]
    [InlineData(16, "16 sessions discovered on this machine")]
    public void SessionHeadingIncludesTheCount(int count, string expected) =>
        Assert.Equal(expected, SettingsPresenter.SessionsHeading(count));

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
    public void AgentChatNamesItsProviderAndUsesItsLastUpdate()
    {
        string line = SettingsPresenter.Describe(
            new SessionSummary(
                "Fix settings",
                Now.AddMinutes(-3),
                AwaitingInput: false,
                CliType.CopilotCli,
                SessionKind.AgentChat,
                "GitHub Copilot"),
            Now);

        Assert.Equal(
            "Fix settings \u2014 GitHub Copilot chat \u2014 updated 3 minutes ago",
            line);
    }

    [Fact]
    public void SessionRowsCarryTheColumnsTheNativeTableNeeds()
    {
        SettingsView view = View(
            sessions:
            [
                new SessionSummary(
                    "build",
                    Now.AddMinutes(-1),
                    AwaitingInput: true,
                    CliType.PowerShell,
                    Program: "pwsh.exe",
                    Cwd: @"C:\source\app"),
            ]);

        SessionRow row = Assert.Single(view.Sessions);
        Assert.Equal("PowerShell", row.Source);
        Assert.Equal(@"C:\source\app", row.Folder);
        Assert.Equal("Waiting for input", row.Status);
        Assert.Equal(Now.AddMinutes(-1), row.ActivityAt);
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

    [Fact]
    public void ArchivedSessionPreferenceHasTheRequestedLabel() =>
        Assert.Equal(
            "Show only sessions visible in GitHub Copilot",
            SettingsPresenter.HideArchivedSessionsLabel);

    [Fact]
    public void AutomaticUpdatePreferenceHasTheRequestedLabel() =>
        Assert.Equal("Automatically update 1RemoteCLI", SettingsPresenter.AutomaticUpdatesLabel);

    private static SettingsView WithUpdate(UpdateStatus update) =>
        SettingsPresenter.Present(AgentState.Connected, "ada@example.com", [], Now, update);

    /// <summary>
    /// The window is silent until the first check completes; it must not claim the
    /// machine is current before it has reached the release service.
    /// </summary>
    [Fact]
    public void SaysNothingAboutUpdatesWhenThereIsNothingToSay()
    {
        SettingsView view = View();

        Assert.Equal(string.Empty, view.Update);
        Assert.False(view.CanUpdate);
        Assert.True(view.CanCheckForUpdates);
    }

    [Fact]
    public void ConfirmsWhenTheInstalledVersionIsCurrent() =>
        Assert.Equal(
            "You're up to date.",
            WithUpdate(new UpdateStatus(UpdateStage.UpToDate)).Update);

    [Fact]
    public void NamesTheVersionThatIsWaiting()
    {
        // The number, not "an update": it is what tells somebody whether this is the
        // release with the fix they have been waiting for.
        SettingsView view = WithUpdate(new UpdateStatus(UpdateStage.Available, "0.13"));

        Assert.Equal("Version 0.13 is available.", view.Update);
        Assert.True(view.CanUpdate);
    }

    /// <summary>
    /// The button is live only when a click would do something. Everything else here is
    /// a report on work already under way.
    /// </summary>
    [Theory]
    [InlineData(UpdateStage.Checking)]
    [InlineData(UpdateStage.Installing)]
    [InlineData(UpdateStage.Restart)]
    [InlineData(UpdateStage.Failed)]
    public void TheButtonIsDeadWhileThereIsNothingToClick(UpdateStage stage) =>
        Assert.False(WithUpdate(new UpdateStatus(stage, "0.13", "something")).CanUpdate);

    [Theory]
    [InlineData(UpdateStage.Checking)]
    [InlineData(UpdateStage.Installing)]
    [InlineData(UpdateStage.Restart)]
    public void AnotherCheckIsDisabledWhileUpdateWorkIsInProgress(UpdateStage stage) =>
        Assert.False(WithUpdate(new UpdateStatus(stage, "0.13")).CanCheckForUpdates);

    [Theory]
    [InlineData(UpdateStage.NotChecked)]
    [InlineData(UpdateStage.UpToDate)]
    [InlineData(UpdateStage.Available)]
    [InlineData(UpdateStage.Failed)]
    public void AnotherCheckIsAllowedWhenNoUpdateWorkIsInProgress(UpdateStage stage) =>
        Assert.True(WithUpdate(new UpdateStatus(stage, "0.13")).CanCheckForUpdates);

    [Fact]
    public void SaysWhatItIsDoing()
    {
        Assert.Equal("Checking for updates\u2026", WithUpdate(new UpdateStatus(UpdateStage.Checking)).Update);
        Assert.Equal(
            "Installing version 0.13\u2026",
            WithUpdate(new UpdateStatus(UpdateStage.Installing, "0.13")).Update);
    }

    /// <summary>
    /// When sessions are in the way the reason is the whole content of the line —
    /// without it this reads as the update having half-worked, and the reader's next
    /// move is to go looking for a fault that is not there.
    /// </summary>
    [Fact]
    public void PassesOnWhyItIsWaiting()
    {
        const string waiting = "Installed. It starts running when the session on this machine has finished.";

        Assert.Equal(waiting, WithUpdate(new UpdateStatus(UpdateStage.Restart, "0.13", waiting)).Update);
    }

    [Fact]
    public void SaysWhyAFailedUpdateFailed()
    {
        SettingsView view = WithUpdate(
            new UpdateStatus(UpdateStage.Failed, "0.13", "The download does not match its checksum."));

        Assert.Equal("The download does not match its checksum.", view.Update);
    }

    [Fact]
    public void HasSomethingToSayAboutAFailureThatCameWithNoReason() =>
        Assert.NotEqual(string.Empty, WithUpdate(new UpdateStatus(UpdateStage.Failed)).Update);
}
