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
/// stays silent until the program writes something new; without that, a prompt left
/// unanswered overnight would notify once a second until morning, and the user would
/// turn notifications off and never turn them back on. That single sentence is worth
/// more to the product than any amount of extra sensitivity.
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
    /// Sessions already announced, cleared when they produce output again.
    /// <para>
    /// Keyed by session id and pruned on every sweep. A session that ends takes its
    /// entry with it, so a machine left running for weeks does not accumulate one row
    /// per terminal anybody ever opened.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, long> _announced = new(StringComparer.Ordinal);

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

            if (_announced.TryGetValue(session.SessionId, out long announcedAt) && announcedAt == lastOutput)
            {
                continue;
            }

            ScreenPosture posture = session.Screen.Posture();

            var signals = new IdleSignals(
                now - session.StartedUtc,
                now - session.LastOutputUtc,
                posture);

            AwaitingInputVerdict verdict = AwaitingInputHeuristic.Evaluate(signals, _options, _patterns);
            if (verdict == AwaitingInputVerdict.No)
            {
                continue;
            }

            // Recorded before the send, not after. The sink talks to the network, so it
            // can be slow or fail; either way this session has had its one announcement
            // for this episode, and retrying is exactly the behaviour to avoid.
            _announced[session.SessionId] = lastOutput;

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
}
