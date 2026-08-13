namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Turns a program's output into frames a phone can survive.
/// <para>
/// A build, an <c>npm install</c>, or a coding agent streaming a long answer can emit
/// megabytes per second. Forwarding each read as it arrives sends thousands of tiny
/// messages a second down a cellular link, each with its own framing and
/// acknowledgement cost, and the phone spends more time on protocol than on drawing.
/// Worse, a full-screen program that redraws at 200 Hz would have every one of those
/// redraws shipped even though 199 of them are overwritten before anyone sees them.
/// </para>
/// <para>
/// So output accumulates and is taken in frames on a fixed tick. The tick is what
/// makes cost proportional to <em>time</em> rather than to how chatty the program is,
/// which is the only property that makes a flood survivable.
/// </para>
/// </summary>
public sealed class OutputCoalescer
{
    /// <summary>
    /// How often frames are taken. 33 ms is ~30 Hz.
    /// <para>
    /// Chosen to sit just under what a person perceives as instant while being long
    /// enough to collapse a redraw storm. Faster buys nothing a human can see and
    /// costs a message; slower starts to feel like lag on the thing the product is
    /// judged on — typing.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// The most that goes in one frame.
    /// <para>
    /// SignalR's default maximum message size is 32 KB. Sitting comfortably under it
    /// rather than at it leaves room for the envelope — session id, sequence number,
    /// kind — without having to reason about MessagePack's exact overhead every time
    /// a field is added.
    /// </para>
    /// </summary>
    public const int MaxFrameBytes = 24 * 1024;

    /// <summary>
    /// How much may pile up behind a boundary that never arrives before the rule is
    /// broken deliberately. Ten frames' worth: long enough that no legitimate escape
    /// sequence comes close, short enough that a program emitting garbage cannot use
    /// this buffer as unbounded memory on the user's own machine.
    /// </summary>
    private const int MaxHeldBytes = MaxFrameBytes * 10;

    private readonly object _gate = new();
    private readonly List<byte> _buffer = new(MaxFrameBytes);

    /// <summary>
    /// How far into the buffer it is safe to cut, or 0 for nowhere.
    /// <para>
    /// Supplied by the emulator's parser rather than computed here. This class knows
    /// about bytes and time; it deliberately knows nothing about VT, because a second
    /// opinion about where a sequence ends is a second thing that can be wrong.
    /// </para>
    /// </summary>
    private int _safeLength;

    /// <summary>Bytes waiting to be sent. Read by backpressure decisions.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>True when something is waiting, whether or not it can be cut yet.</summary>
    public bool HasPending => Pending > 0;

    /// <summary>
    /// Adds output, told where it may be cut.
    /// <para>
    /// <paramref name="lastSafeOffset"/> is an offset within <paramref name="bytes"/>,
    /// or -1 when this batch ended partway through a sequence or a character.
    /// </para>
    /// </summary>
    public void Append(ReadOnlySpan<byte> bytes, int lastSafeOffset)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            int start = _buffer.Count;
            _buffer.AddRange(bytes);

            if (lastSafeOffset >= 0)
            {
                _safeLength = start + lastSafeOffset;
            }
        }
    }

    /// <summary>
    /// Takes the next frame, or returns false when there is nothing sendable yet.
    /// <para>
    /// "Nothing sendable" is not the same as "nothing buffered": output that ends
    /// partway through an escape sequence is held until the rest of it arrives,
    /// because half a sequence is not a partial picture but a wrong one — the client's
    /// parser would take the following text as parameters.
    /// </para>
    /// </summary>
    public bool TryTake(out byte[] frame)
    {
        lock (_gate)
        {
            int take = Math.Min(_safeLength, MaxFrameBytes);

            if (take <= 0)
            {
                // Nothing safe to cut. Normally this resolves on the next tick, when
                // the rest of the sequence has arrived. If it does not, and the buffer
                // has grown past anything a real sequence could justify, the stream is
                // malformed and holding more of it helps nobody: cut anyway and let
                // the client's parser resynchronise, which it is built to do.
                if (_buffer.Count < MaxHeldBytes)
                {
                    frame = [];
                    return false;
                }

                take = MaxFrameBytes;
            }

            frame = new byte[take];
            _buffer.CopyTo(0, frame, 0, take);
            _buffer.RemoveRange(0, take);
            _safeLength = Math.Max(0, _safeLength - take);

            return true;
        }
    }
}
