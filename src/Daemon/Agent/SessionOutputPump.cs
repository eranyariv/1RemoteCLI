namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Drains one session's coalesced output on a fixed tick.
/// <para>
/// Separate from the connection that produces the output on purpose. A pump driven by
/// arrivals would flush as fast as the program writes, which is the behaviour the
/// coalescer exists to prevent; the tick has to come from a clock, not from the data.
/// </para>
/// </summary>
public sealed class SessionOutputPump
{
    private readonly TerminalSession _session;
    private readonly ISessionSink _sink;
    private readonly TimeSpan _tick;

    public SessionOutputPump(TerminalSession session, ISessionSink sink, TimeSpan? tick = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tick = tick ?? OutputCoalescer.Tick;
    }

    /// <summary>Runs until cancelled, then flushes whatever was left.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_tick);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Sends everything currently sendable.
    /// <para>
    /// Loops rather than sending one frame per tick, because a burst larger than a
    /// frame would otherwise drain at 24 KB per 33 ms — about 700 KB/s — and a build's
    /// output would arrive minutes after it was produced, which is worse than useless
    /// because it looks like the build is still running.
    /// </para>
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] frame = [];

            // Taking the frame and sending it happen together, so a snapshot cannot be
            // taken between them and arrive describing a screen that already includes
            // output the client is about to be sent separately.
            await _session.RunExclusiveAsync(
                async () =>
                {
                    if (!_session.Output.TryTake(out frame))
                    {
                        return;
                    }

                    await _sink.OnOutputAsync(_session, frame, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (frame.Length == 0)
            {
                return;
            }
        }
    }
}
