using System.Text;
using OneRemoteCli.Daemon.Agent;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The sweep, driven against a real VT emulator rather than a hand-built posture, so
/// that the escape sequences a coding agent actually emits are part of what is
/// tested. A screen assembled by hand would agree with the heuristic by construction.
/// </summary>
public sealed class AwaitingInputMonitorTests
{
    /// <summary>
    /// Long enough to be quiet by any of the thresholds.
    /// <para>
    /// Every session here runs <c>claude</c>, so it is judged by the agent quiet
    /// period rather than the shorter one a shell gets. These tests are about what
    /// the sweep decides once a screen has gone quiet, not about how long that
    /// takes, so they say so in one place.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PastTheQuietPeriod = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_permission_prompt_is_reported_once_the_screen_goes_quiet()
    {
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "Reading src/Daemon/Agent/AgentHost.cs\r\n? Allow Copilot to edit this file? (y/n) ");
        world.Advance(PastTheQuietPeriod);

        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);
        Assert.Equal(session.SessionId, world.Sink.Awaiting[0].SessionId);
        Assert.Contains("(y/n)", world.Sink.Awaiting[0].Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticking_status_bar_does_not_re_announce_the_same_prompt()
    {
        // The bug this exists for. A coding agent redraws a footer carrying a spinner,
        // an elapsed time and a token count, so the screen is never byte-identical two
        // sweeps running. Hashing the whole screen made every tick a new episode, and
        // one unanswered prompt notified the user over and over -- which is precisely
        // the training-to-ignore that the heuristic is written to avoid.
        var world = new World();
        TerminalSession session = world.Session();

        const string prompt = "[2J[HAllow this edit? (y/n) ";
        world.Write(session, prompt);
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);

        // Same prompt, same cursor, only the footer moved on.
        for (int seconds = 40; seconds < 44; seconds++)
        {
            world.Write(session, $"7[24;1HElucidating... ({seconds}s · {seconds * 30} tokens)8");
            world.Advance(PastTheQuietPeriod);
            await world.Monitor.SweepAsync();
        }

        Assert.Single(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_build_that_is_merely_slow_is_not_reported()
    {
        // Same silence, same visible cursor, no prompt: the last line ended with a
        // newline, so the cursor is at column zero of a blank row.
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "  Determining projects to restore...\r\n  Restoring packages...\r\n");
        world.Advance(TimeSpan.FromMinutes(5));

        await world.Monitor.SweepAsync();

        Assert.Empty(world.Sink.Awaiting);
        Assert.False(session.Screen.Posture().CursorAfterText);
    }

    [Fact]
    public async Task A_session_is_reported_once_per_quiet_episode()
    {
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "Continue? ");
        world.Advance(PastTheQuietPeriod);

        await world.Monitor.SweepAsync();
        world.Advance(TimeSpan.FromMinutes(30));
        await world.Monitor.SweepAsync();
        await world.Monitor.SweepAsync();

        // Half an hour of an unanswered prompt is one notification, not eighteen
        // hundred. Without this the user turns notifications off and never turns them
        // back on, which costs the whole feature.
        Assert.Single(world.Sink.Awaiting);
    }

    [Fact]
    public async Task New_output_re_arms_the_session()
    {
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "Continue? ");
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        world.Write(session, "y\r\nDone. Next? ");
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        Assert.Equal(2, world.Sink.Awaiting.Count);
        Assert.Contains("Next?", world.Sink.Awaiting[1].Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identical_full_screen_redraw_does_not_re_arm_the_session()
    {
        var world = new World();
        TerminalSession session = world.Session();

        const string screen = "\u001b[2J\u001b[HClaude Code\r\n\u25b6\u25b6 auto mode on (shift+tab to cycle)\r\n  \u00b7 \u2190 for agents";
        world.Write(session, screen);
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        world.Write(session, screen);
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_session_that_has_only_just_started_is_not_reported()
    {
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "> ");
        world.Advance(TimeSpan.FromSeconds(2));

        await world.Monitor.SweepAsync();

        Assert.Empty(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_hidden_cursor_is_not_reported()
    {
        var world = new World();
        TerminalSession session = world.Session();

        // DECTCEM off: what a full-screen renderer does while it paints.
        world.Write(session, "\u001b[?25lContinue? ");
        world.Advance(TimeSpan.FromSeconds(30));

        await world.Monitor.SweepAsync();

        Assert.Empty(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_configured_pattern_reports_before_the_quiet_period()
    {
        var world = new World(AwaitingInputOptions.Default with { PromptPatterns = ["press any key"] });
        TerminalSession session = world.Session();

        world.Write(session, "Press any key to continue . . .\r\n");
        world.Advance(TimeSpan.FromSeconds(6));

        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_sink_that_throws_does_not_stop_the_sweep()
    {
        // A session at the desk must never fail because a notification could not be
        // delivered; the hub is frequently unreachable and that is normal.
        var world = new World(sink: new AngrySink());
        TerminalSession session = world.Session();

        world.Write(session, "Continue? ");
        world.Advance(PastTheQuietPeriod);

        await world.Monitor.SweepAsync();

        Assert.Contains(world.Log, line => line.Contains("not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_restored_after_a_restart_does_not_announce_the_screen_it_came_back_with()
    {
        // Issue #183. An auto-update restarts the agent, every surviving wrapper
        // reconnects, and the wrapper replays the screen the session already had. That
        // replay is not a new quiet episode -- the idle input box on it has been idle
        // all along -- but a fresh registry could not tell the difference, so a single
        // update sent one push per open session on the machine.
        var world = new World();
        TerminalSession session = world.Restored();

        world.Write(session, "auto mode on (shift+tab to cycle) ");
        world.Advance(PastTheQuietPeriod);

        await world.Monitor.SweepAsync();

        Assert.Empty(world.Sink.Awaiting);
    }

    [Fact]
    public async Task A_restored_session_still_reports_a_prompt_that_arrives_after_it_came_back()
    {
        // The other half of the rule above, and what keeps it from being a mute button:
        // the screen a session returned with is old news exactly once. A prompt the
        // agent asks afterwards is the thing the user is waiting to hear about.
        var world = new World();
        TerminalSession session = world.Restored();

        world.Write(session, "auto mode on (shift+tab to cycle) ");
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        Assert.Empty(world.Sink.Awaiting);

        world.Write(session, "Allow this edit? (y/n) ");
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);
        Assert.Contains("(y/n)", world.Sink.Awaiting[0].Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_ends_is_forgotten()
    {
        var world = new World();
        TerminalSession session = world.Session();

        world.Write(session, "Continue? ");
        world.Advance(PastTheQuietPeriod);
        await world.Monitor.SweepAsync();

        world.Sessions.Remove(session.SessionId);
        await world.Monitor.SweepAsync();

        Assert.Single(world.Sink.Awaiting);
    }

    [Fact]
    public void The_quiet_period_can_be_changed_without_a_rebuild()
    {
        string path = Path.Combine(Path.GetTempPath(), $"1remote-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, """{ "awaitingInput": { "quietPeriodSeconds": 2, "promptPatterns": ["\\?$"] } }""");

        try
        {
            AwaitingInputOptions fromFile = AwaitingInputOptions.Load(path);
            Assert.Equal(TimeSpan.FromSeconds(2), fromFile.QuietPeriod);
            Assert.Equal(["\\?$"], fromFile.PromptPatterns);

            AwaitingInputOptions overridden = AwaitingInputOptions.Load(
                path,
                new Dictionary<string, string?> { ["ONEREMOTE_QUIET_PERIOD_SECONDS"] = "45" });
            Assert.Equal(TimeSpan.FromSeconds(45), overridden.QuietPeriod);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_settings_file_that_is_not_valid_json_costs_the_setting_and_nothing_else()
    {
        string path = Path.Combine(Path.GetTempPath(), $"1remote-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            var complaints = new List<string>();
            AwaitingInputOptions options = AwaitingInputOptions.Load(path, log: complaints.Add);

            Assert.Equal(AwaitingInputOptions.Default.QuietPeriod, options.QuietPeriod);
            Assert.NotEmpty(complaints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Internals.

    private sealed class World
    {
        public World(AwaitingInputOptions? options = null, RecordingSink? sink = null)
        {
            Sink = sink ?? new RecordingSink();
            Time = new SteppingClock(DateTimeOffset.UtcNow);
            Monitor = new AwaitingInputMonitor(Sessions, Sink, options, Time, Log.Add);
        }

        public SessionRegistry Sessions { get; } = new();

        public RecordingSink Sink { get; }

        public SteppingClock Time { get; }

        public AwaitingInputMonitor Monitor { get; }

        public List<string> Log { get; } = [];

        public TerminalSession Session() =>
            Sessions.Add("claude", [], @"C:\repo", 80, 24, "claude", new SilentChannel());

        /// <summary>
        /// A session as it exists after an agent restart: a wrapper reconnected and
        /// reclaimed its prior id, which is what marks the screen it brings back as
        /// something the user has already seen.
        /// </summary>
        public TerminalSession Restored() =>
            Sessions.Add(
                "claude",
                [],
                @"C:\repo",
                80,
                24,
                "claude",
                new SilentChannel(),
                priorSessionId: Guid.NewGuid().ToString("n"));

        public void Write(TerminalSession session, string text)
        {
            session.Screen.Feed(Encoding.UTF8.GetBytes(text));
            session.NoteOutput(Time.GetUtcNow());
        }

        public void Advance(TimeSpan by) => Time.Advance(by);
    }

    /// <summary>
    /// A clock the test moves by hand.
    /// <para>
    /// Hand-rolled rather than taken from a package: the monitor only ever asks for
    /// the time, and adding a dependency to the test project to supply one method
    /// would be a worse trade than eight lines.
    /// </para>
    /// </summary>
    private sealed class SteppingClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private class RecordingSink : ISessionSink
    {
        public List<(string SessionId, string? Hint)> Awaiting { get; } = [];

        public ValueTask OnOpenedAsync(TerminalSession session, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnOutputAsync(
            TerminalSession session,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnClosedAsync(
            TerminalSession session,
            int exitCode,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public virtual ValueTask OnAwaitingInputAsync(
            TerminalSession session,
            string? hint,
            CancellationToken cancellationToken = default)
        {
            Awaiting.Add((session.SessionId, hint));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AngrySink : RecordingSink
    {
        public override ValueTask OnAwaitingInputAsync(
            TerminalSession session,
            string? hint,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the hub is not there");
    }

    private sealed class SilentChannel : ISessionChannel
    {
        public ValueTask SendInputAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
