using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>One frame that was numbered for this session, kept in case it has to be sent again.</summary>
public readonly record struct TailFrame(long Seq, TerminalOutputKind Kind, byte[] Data);

/// <summary>
/// Numbers a session's outbound frames and remembers the recent ones.
/// <para>
/// A phone loses signal constantly — a lift, a tunnel, a locked screen. Almost all of
/// those gaps are seconds long, and during a gap of seconds a terminal produces very
/// little. Keeping that little means a reattach can send exactly what was missed, and
/// the user sees no interruption at all. Without it every blink costs a full repaint,
/// which is both slower and visibly a reset.
/// </para>
/// <para>
/// The buffer is small and its eviction is crude on purpose. Falling out of it is not a
/// failure: it means the reattach is answered with a snapshot instead, which is also
/// correct, just less pretty. That is what allows the memory bound to be absolute
/// rather than a guess at how long a disconnection might last.
/// </para>
/// <para>
/// Frames are numbered here rather than at the moment of sending, so output produced
/// while the hub is unreachable still consumes a sequence number and is still retained.
/// Otherwise a reattach after a hub outage would replay a contiguous-looking run of
/// frames with the outage's output silently missing from it — a screen that is wrong
/// while claiming to be continuous, which is the one outcome worse than a repaint.
/// </para>
/// </summary>
public sealed class SessionTail
{
    /// <summary>
    /// How much history is kept. 256 KB is several screens' worth of repaints, so it
    /// covers the disconnections that actually happen, and it is small enough that
    /// every session on a busy machine can afford one.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly Queue<TailFrame> _frames = new();

    private long _seq;
    private int _bytes;

    /// <summary>The highest sequence number handed out so far.</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>Bytes currently retained. Never above <see cref="MaxBytes"/> for more than one frame.</summary>
    public int RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Numbers a frame and keeps it, evicting the oldest until the buffer fits again.</summary>
    public long Record(TerminalOutputKind kind, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        lock (_gate)
        {
            long seq = ++_seq;

            _frames.Enqueue(new TailFrame(seq, kind, data));
            _bytes += data.Length;

            // Count > 1 so a single oversized frame is retained rather than leaving the
            // buffer empty. It cannot happen with framed output, but a buffer that can
            // evict its way to nothing is a worse thing to reason about than one frame
            // over budget.
            while (_bytes > MaxBytes && _frames.Count > 1)
            {
                _bytes -= _frames.Dequeue().Data.Length;
            }

            return seq;
        }
    }

    /// <summary>
    /// Works out what a returning client missed.
    /// <para>
    /// Returns false when the answer has to be a snapshot: the client is asking from
    /// before what is still retained, or from a sequence this session never issued —
    /// which is what a client that reconnected to a restarted agent looks like.
    /// </para>
    /// </summary>
    public bool TryReplayFrom(long lastSeq, out IReadOnlyList<TailFrame> missed)
    {
        lock (_gate)
        {
            if (lastSeq < 0 || lastSeq > _seq)
            {
                missed = [];
                return false;
            }

            if (lastSeq == _seq)
            {
                // Nothing happened while it was away. Still a resume: its screen is
                // already the truth, and repainting it would be a visible flicker for
                // no information at all.
                missed = [];
                return true;
            }

            if (_frames.Count == 0 || lastSeq + 1 < _frames.Peek().Seq)
            {
                missed = [];
                return false;
            }

            var frames = new List<TailFrame>();

            foreach (TailFrame frame in _frames)
            {
                if (frame.Seq > lastSeq)
                {
                    frames.Add(frame);
                }
            }

            missed = frames;
            return true;
        }
    }
}
