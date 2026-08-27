using System.Text.RegularExpressions;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Watches every session on the machine and says, at most once per quiet episode,
/// that one of them is waiting for the user.
/// <para>
/// Polls rather than reacting to output, because the event being detected is the
/// <em>absence</em> of output, and nothing arrives to announce it. A sweep is a few
/// field reads per session on a one-second tick, against a machine that has perhaps
/// a dozen sessions - too cheap to be worth complicating.
/// </para>
/// <para>
/// The arming rule is what stops this being noise. A session that has been announced
/// stays silent until the rendered screen meaningfully changes. Counting raw output
/// is not enough because full-screen CLIs periodically repaint an unchanged view;
/// treating those redraws as activity repeats the same notification until the user
/// turns notifications off. That single rule is worth more to the product than any
/// amount of extra sensitivity.
/// </para>
/// </summary>
public sealed class AwaitingInputMonitor
{
    private readonly SessionRegistry _sessions;
    private readonly ISessionSink _sink;
    private readonly AwaitingInputOptions _options;
    private readonly IReadOnlyList<Regex> _patterns;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    /// <summary>
    /// Notification state for sessions already announced, re-armed by a changed screen.
    /// <para>
    /// Keyed by session id and pruned on every sweep. A session that ends takes its
    /// entry with it, so a machine left running for weeks does not accumulate one row
    /// per terminal anybody ever opened.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, AnnouncementState> _announced = new(StringComparer.Ordinal);

    public AwaitingInputMonitor(
        SessionRegistry sessions,
        ISessionSink sink,
        AwaitingInputOptions? options = null,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? AwaitingInputOptions.Default;
        _time = time ?? TimeProvider.System;
        _log = log;
        _patterns = _options.CompilePatterns(log);
    }

    /// <summary>Sweeps until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                await SweepAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass over every session. Public so a test can drive it without a clock.</summary>
    public async ValueTask SweepAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TerminalSession> sessions = _sessions.Snapshot();

        // A session only exists here while its wrapper is connected, and the wrapper
        // owns the child process. So "the child is still running" - one of the four
        // conditions - is structural rather than something to check.
        Forget(sessions);

        DateTimeOffset now = _time.GetUtcNow();

        foreach (TerminalSession session in sessions)
        {
            long lastOutput = session.OutputCount;
            bool wasAnnounced = _announced.TryGetValue(
                session.SessionId,
                out AnnouncementState announced);

            if (wasAnnounced && announced.OutputCount == lastOutput && !announced.Armed)
            {
                continue;
            }

            ScreenPosture posture;
            string? fingerprint = null;

            if (wasAnnounced && announced.OutputCount != lastOutput)
            {
                AwaitingInputScreen screen = session.Screen.AwaitingInputScreen();
                posture = screen.Posture;
                fingerprint = screen.Fingerprint;

                // Interactive CLIs redraw status bars and menus even when nothing
                // visible changed. Those bytes advance OutputCount, but they are not a
                // new quiet episode and must not re-arm the same notification.
                announced = announced.Fingerprint == fingerprint
                    ? announced with { OutputCount = lastOutput }
                    : new AnnouncementState(lastOutput, fingerprint, Armed: true);
                _announced[session.SessionId] = announced;

                if (!announced.Armed)
                {
                    continue;
                }
            }
            else
            {
                // The common path needs only one row and the cursor state. Serialising
                // and hashing the full screen is reserved for redraw comparison and
                // recording an actual announcement.
                posture = session.Screen.Posture();
            }

            var signals = new IdleSignals(
                now - session.StartedUtc,
                now - session.LastOutputUtc,
                posture);

            // What is running decides how long silence has to last before it means
            // anything. An agent pausing to think is not a shell sitting at a prompt.
            AwaitingInputOptions options = _options.ForCliType(session.CliType);

            AwaitingInputVerdict verdict = AwaitingInputHeuristic.Evaluate(signals, options, _patterns);
            if (verdict == AwaitingInputVerdict.No)
            {
                continue;
            }

            if (fingerprint is null)
            {
                // Re-read posture together with the fingerprint. Output may have landed
                // after the cheap check above, and the state we record must describe the
                // exact screen whose prompt verdict caused the announcement.
                AwaitingInputScreen screen = session.Screen.AwaitingInputScreen();
                posture = screen.Posture;
                fingerprint = screen.Fingerprint;
                signals = new IdleSignals(
                    now - session.StartedUtc,
                    now - session.LastOutputUtc,
                    posture);
                verdict = AwaitingInputHeuristic.Evaluate(signals, options, _patterns);

                if (verdict == AwaitingInputVerdict.No)
                {
                    continue;
                }
            }

            // Recorded before the send, not after. The sink talks to the network, so it
            // can be slow or fail; either way this session has had its one announcement
            // for this episode, and retrying is exactly the behaviour to avoid.
            _announced[session.SessionId] =
                new AnnouncementState(lastOutput, fingerprint, Armed: false);

            _log?.Invoke($"awaiting-input: {session.DisplayName} ({verdict.ToString().ToLowerInvariant()}).");

            try
            {
                await _sink.OnAwaitingInputAsync(
                    session,
                    AwaitingInputHeuristic.Hint(posture),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A session must never fail at the desk because a notification could
                // not be delivered.
                _log?.Invoke($"awaiting-input: not delivered ({ex.Message}).");
            }
        }
    }

    /// <summary>
    /// Whether a session looks like it is waiting for the user, right now.
    /// <para>
    /// The same judgement the sweep makes, without the arming rule that keeps a
    /// notification from repeating. A notification fires once per quiet episode; a
    /// window shows what is true while it is open, and one that stopped saying
    /// "waiting" the moment it had said it once would be wrong for as long as the
    /// prompt stayed unanswered.
    /// </para>
    /// <para>
    /// Lives here rather than in the settings window because the compiled patterns and
    /// the thresholds are here, and a second copy of the heuristic would eventually
    /// disagree with the notifications about the same session.
    /// </para>
    /// </summary>
    public bool IsAwaitingInput(TerminalSession session, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        DateTimeOffset instant = now ?? _time.GetUtcNow();

        var signals = new IdleSignals(
            instant - session.StartedUtc,
            instant - session.LastOutputUtc,
            session.Screen.Posture());

        return AwaitingInputHeuristic.Evaluate(signals, _options, _patterns) != AwaitingInputVerdict.No;
    }

    private void Forget(IReadOnlyList<TerminalSession> sessions)
    {
        if (_announced.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(sessions.Select(s => s.SessionId), StringComparer.Ordinal);
        foreach (string id in _announced.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _announced.Remove(id);
        }
    }

    private readonly record struct AnnouncementState(long OutputCount, string Fingerprint, bool Armed);
}
