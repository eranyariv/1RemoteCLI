namespace OneRemoteCli.Terminal.Vt;

/// <summary>
/// The states of the Paul Williams ANSI/DEC parser.
/// <para>
/// Exposed rather than hidden because <see cref="VtParser.State"/> is load-bearing
/// outside the parser: output framing may only cut a frame while the parser is in
/// <see cref="Ground"/>, which is what guarantees a frame never ends halfway through
/// an escape sequence. A parser that kept its state private would force the framing
/// layer to re-derive it, and two implementations of "am I mid-sequence?" would
/// eventually disagree.
/// </para>
/// </summary>
public enum VtState
{
    /// <summary>Ordinary text. The only state in which a stream may be cut.</summary>
    Ground,

    /// <summary>Just saw <c>ESC</c>.</summary>
    Escape,

    /// <summary>Collecting intermediates for an <c>ESC</c> sequence.</summary>
    EscapeIntermediate,

    /// <summary>Just saw <c>CSI</c>; nothing collected yet.</summary>
    CsiEntry,

    /// <summary>Collecting numeric parameters of a CSI sequence.</summary>
    CsiParam,

    /// <summary>Collecting intermediates of a CSI sequence.</summary>
    CsiIntermediate,

    /// <summary>The CSI sequence is malformed or too long; consume it and dispatch nothing.</summary>
    CsiIgnore,

    /// <summary>Just saw <c>DCS</c>.</summary>
    DcsEntry,

    /// <summary>Collecting numeric parameters of a DCS sequence.</summary>
    DcsParam,

    /// <summary>Collecting intermediates of a DCS sequence.</summary>
    DcsIntermediate,

    /// <summary>Inside a DCS payload; bytes are handed to the sink one at a time.</summary>
    DcsPassthrough,

    /// <summary>The DCS sequence is malformed; consume it and report nothing.</summary>
    DcsIgnore,

    /// <summary>Inside an OSC payload, such as a window title.</summary>
    OscString,

    /// <summary>Inside SOS, PM or APC. Consumed and discarded.</summary>
    SosPmApcString,
}
