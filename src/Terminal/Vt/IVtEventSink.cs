using System.Text;

namespace OneRemoteCli.Terminal.Vt;

/// <summary>
/// What <see cref="VtParser"/> reports. The parser holds no screen state; everything
/// it understands about the stream arrives here.
/// <para>
/// The split matters: a state machine that also maintained a grid would be impossible
/// to test against the two properties that make it trustworthy — that it never throws
/// on arbitrary bytes, and that it produces the same events no matter how the input is
/// chunked. Both are statements about events, so events have to be a first-class thing
/// the parser produces rather than an internal detail of a screen.
/// </para>
/// </summary>
public interface IVtEventSink
{
    /// <summary>A printable character. Already decoded from UTF-8, so this is a whole character.</summary>
    void Print(Rune rune);

    /// <summary>A C0 control byte such as carriage return, line feed or backspace.</summary>
    void Execute(byte control);

    /// <summary>
    /// A completed CSI sequence, such as <c>CSI 2 J</c>.
    /// </summary>
    /// <param name="parameters">The numeric parameters, grouped by <c>;</c>.</param>
    /// <param name="intermediates">
    /// Intermediate bytes in the <c>0x20–0x2F</c> range, plus any private marker such
    /// as the <c>?</c> of <c>CSI ? 25 h</c>. Kept rather than folded into a flag because
    /// the private marker changes what the final byte means.
    /// </param>
    /// <param name="final">The final byte, which selects the operation.</param>
    void CsiDispatch(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>A completed <c>ESC</c> sequence, such as <c>ESC 7</c> (save cursor).</summary>
    void EscDispatch(ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>
    /// A completed OSC sequence, payload verbatim and undecoded.
    /// <para>
    /// Undecoded because the payload is not always text: OSC 52 carries base64 and
    /// OSC 4 carries colour specifications. Handing over bytes lets each handler decode
    /// only what it understands.
    /// </para>
    /// </summary>
    void OscDispatch(ReadOnlySpan<byte> data);

    /// <summary>A DCS sequence has started. Payload bytes follow via <see cref="Put"/>.</summary>
    void Hook(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>One byte of a DCS payload.</summary>
    void Put(byte data);

    /// <summary>The DCS payload has ended.</summary>
    void Unhook();
}

/// <summary>
/// An <see cref="IVtEventSink"/> that does nothing, for callers that only want the
/// parser's state — the framing layer decides where to cut a frame without caring what
/// the sequences mean.
/// </summary>
public sealed class NullVtEventSink : IVtEventSink
{
    public static readonly NullVtEventSink Instance = new();

    private NullVtEventSink()
    {
    }

    public void Print(Rune rune)
    {
    }

    public void Execute(byte control)
    {
    }

    public void CsiDispatch(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
    }

    public void EscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
    }

    public void OscDispatch(ReadOnlySpan<byte> data)
    {
    }

    public void Hook(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
    }

    public void Put(byte data)
    {
    }

    public void Unhook()
    {
    }
}
