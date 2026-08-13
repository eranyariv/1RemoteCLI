using System.Text;

namespace OneRemoteCli.Terminal.Vt;

/// <summary>
/// The Paul Williams ANSI/DEC parser: bytes in, terminal events out.
/// <para>
/// Byte-oriented by construction, which is the whole reason for choosing it. Output
/// arrives from a pseudoconsole in arbitrary chunks and crosses a network in arbitrary
/// frames, so a parser that assumed it could see a whole escape sequence — or a whole
/// UTF-8 character — at once would be wrong the first time a 4 KB read landed in the
/// middle of one. Here every byte is handled independently and all continuation state
/// lives on the instance, so feeding <c>N</c> bytes one at a time and feeding them in
/// one call produce identical events. That property is tested directly, because
/// everything downstream depends on it and nothing about it is self-evident.
/// </para>
/// <para>
/// <b>UTF-8, not 8-bit C1.</b> Bytes <c>0x80–0x9F</c> are treated as UTF-8 data rather
/// than as single-byte C1 controls. The two interpretations are mutually exclusive, and
/// every terminal this system will ever see runs in UTF-8; treating <c>0x9B</c> as CSI
/// would corrupt any character whose encoding happens to contain that byte, which
/// includes a great deal of ordinary text. The seven-bit spellings (<c>ESC [</c>,
/// <c>ESC ]</c>, and so on) are the ones programs actually emit.
/// </para>
/// <para>
/// The parser never throws. A hostile or merely broken stream is an ordinary event
/// here — a session's output is attacker-influenced by definition, since it contains
/// whatever the user ran — so malformed input is consumed and discarded rather than
/// raised. Anything else would let one bad byte take down a session.
/// </para>
/// </summary>
public sealed class VtParser
{
    /// <summary>
    /// Past this many parameters a CSI sequence is abandoned. The DEC limit is 16; the
    /// margin exists because SGR sequences with sub-parameters legitimately run long.
    /// </summary>
    private const int MaxParams = 32;

    /// <summary>Total parameter values including sub-parameters.</summary>
    private const int MaxValues = 64;

    /// <summary>
    /// DEC allows two intermediates. A third means the sequence is malformed, and the
    /// right response is to swallow the rest of it rather than guess.
    /// </summary>
    private const int MaxIntermediates = 2;

    /// <summary>
    /// A parameter is clamped rather than allowed to overflow. Sixteen bits is what
    /// every other terminal uses, and a clamped value produces a wrong-but-bounded
    /// screen where an overflowed one produces a negative row index.
    /// </summary>
    private const int MaxParamValue = 65535;

    /// <summary>
    /// How much of an OSC payload is kept. Titles are short; anything longer is either
    /// a clipboard write this system does not implement or a stream gone wrong, and
    /// neither justifies unbounded memory on a per-session buffer.
    /// </summary>
    private const int MaxOscLength = 8192;

    private readonly int[] _values = new int[MaxValues];
    private readonly int[] _starts = new int[MaxParams];
    private readonly byte[] _intermediates = new byte[MaxIntermediates];
    private readonly List<byte> _osc = new(64);

    private int _valueCount;
    private int _paramCount;
    private int _intermediateCount;
    private bool _valueOpen;
    private bool _paramOpen;
    private bool _oscTruncated;

    // Incremental UTF-8 decoding. Held across calls so a character split by a chunk
    // boundary survives it.
    private int _pending;
    private int _pendingNeeded;
    private int _pendingSeen;
    private int _pendingLowerBound;

    /// <summary>Where the state machine currently is.</summary>
    public VtState State { get; private set; } = VtState.Ground;

    /// <summary>
    /// True when the stream may be cut here without corrupting anything: the parser is
    /// in <see cref="VtState.Ground"/> <em>and</em> not partway through a UTF-8
    /// character. Both halves matter — a frame that ends between the two bytes of "é"
    /// is just as broken as one that ends between <c>ESC</c> and <c>[</c>, and only the
    /// parser knows about either.
    /// </summary>
    public bool IsAtSafeBoundary => State == VtState.Ground && _pendingNeeded == 0;

    /// <summary>Feeds bytes to the parser, reporting what they mean to <paramref name="sink"/>.</summary>
    public void Parse(ReadOnlySpan<byte> bytes, IVtEventSink sink) => Parse(bytes, sink, out _);

    /// <summary>
    /// Feeds bytes and reports where the last cuttable point was.
    /// <para>
    /// <paramref name="lastSafeOffset"/> is the number of bytes from the start of
    /// <paramref name="bytes"/> that may be taken without splitting a sequence or a
    /// character; -1 means there was no such point in this batch. Only the parser can
    /// answer this, and only while it is running — asking afterwards gives the state
    /// at the very end, which says nothing about the eleven kilobytes before it.
    /// </para>
    /// <para>
    /// This exists so output can be cut into frames for the network. A frame that ends
    /// mid-sequence is not merely inelegant: a client that reconnects and resumes from
    /// a frame boundary would start reading in the middle of an escape sequence, and
    /// every byte after it would be interpreted as something else.
    /// </para>
    /// </summary>
    public void Parse(ReadOnlySpan<byte> bytes, IVtEventSink sink, out int lastSafeOffset)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lastSafeOffset = -1;

        for (int i = 0; i < bytes.Length; i++)
        {
            Advance(bytes[i], sink);

            if (IsAtSafeBoundary)
            {
                lastSafeOffset = i + 1;
            }
        }
    }

    /// <summary>
    /// Returns the parser to <see cref="VtState.Ground"/>, discarding any sequence in
    /// progress. For a session being restarted, not for error recovery: the parser
    /// recovers from bad input on its own.
    /// </summary>
    public void Reset()
    {
        State = VtState.Ground;
        _osc.Clear();
        _oscTruncated = false;
        _pending = 0;
        _pendingNeeded = 0;
        _pendingSeen = 0;
        _pendingLowerBound = 0;
        ClearSequence();
    }

    private void Advance(byte b, IVtEventSink sink)
    {
        // A partially decoded character is only meaningful in Ground; anything else
        // means the stream moved on without finishing it.
        if (_pendingNeeded > 0 && State == VtState.Ground)
        {
            if (b is >= 0x80 and <= 0xBF)
            {
                ContinueUtf8(b, sink);
                return;
            }

            // Truncated. Report the damage and reprocess this byte from scratch, which
            // is what makes "ESC in the middle of a character" behave sanely.
            EmitReplacement(sink);
        }

        // Cancel and substitute abandon whatever is in progress, everywhere.
        if (b is 0x18 or 0x1A)
        {
            LeaveState(sink);
            sink.Execute(b);
            State = VtState.Ground;
            return;
        }

        if (b == 0x1B)
        {
            LeaveState(sink);
            ClearSequence();
            State = VtState.Escape;
            return;
        }

        switch (State)
        {
            case VtState.Ground:
                Ground(b, sink);
                break;

            case VtState.Escape:
                Escape(b, sink);
                break;

            case VtState.EscapeIntermediate:
                EscapeIntermediate(b, sink);
                break;

            case VtState.CsiEntry:
                CsiEntry(b, sink);
                break;

            case VtState.CsiParam:
                CsiParam(b, sink);
                break;

            case VtState.CsiIntermediate:
                CsiIntermediate(b, sink);
                break;

            case VtState.CsiIgnore:
                CsiIgnore(b, sink);
                break;

            case VtState.DcsEntry:
                DcsEntry(b, sink);
                break;

            case VtState.DcsParam:
                DcsParam(b, sink);
                break;

            case VtState.DcsIntermediate:
                DcsIntermediate(b, sink);
                break;

            case VtState.DcsPassthrough:
                DcsPassthrough(b, sink);
                break;

            case VtState.DcsIgnore:
                break;

            case VtState.OscString:
                OscString(b, sink);
                break;

            case VtState.SosPmApcString:
                break;

            default:
                State = VtState.Ground;
                break;
        }
    }

    // States.

    private void Ground(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        if (b < 0x80)
        {
            sink.Print(new Rune(b));
            return;
        }

        BeginUtf8(b, sink);
    }

    private void Escape(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                State = VtState.EscapeIntermediate;
                return;

            case 0x50: // P — DCS
                State = VtState.DcsEntry;
                return;

            case 0x58: // X — SOS
            case 0x5E: // ^ — PM
            case 0x5F: // _ — APC
                State = VtState.SosPmApcString;
                return;

            case 0x5B: // [ — CSI
                State = VtState.CsiEntry;
                return;

            case 0x5D: // ] — OSC
                _osc.Clear();
                _oscTruncated = false;
                State = VtState.OscString;
                return;

            case 0x7F:
                return;

            case >= 0x30 and <= 0x7E:
                sink.EscDispatch(Intermediates, b);
                State = VtState.Ground;
                return;

            default:
                // A byte with the high bit set here cannot be part of any sequence.
                // Swallowing it is the point: letting it through would paint mojibake.
                State = VtState.Ground;
                return;
        }
    }

    private void EscapeIntermediate(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                return;

            case 0x7F:
                return;

            case >= 0x30 and <= 0x7E:
                sink.EscDispatch(Intermediates, b);
                State = VtState.Ground;
                return;

            default:
                State = VtState.Ground;
                return;
        }
    }

    private void CsiEntry(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                State = VtState.CsiIntermediate;
                return;

            case >= 0x30 and <= 0x39:
                Digit(b);
                State = VtState.CsiParam;
                return;

            case 0x3A: // : — sub-parameter separator
                SubParamSeparator();
                State = VtState.CsiParam;
                return;

            case 0x3B: // ;
                ParamSeparator();
                State = VtState.CsiParam;
                return;

            case >= 0x3C and <= 0x3F: // < = > ? — private markers
                Collect(b);
                State = VtState.CsiParam;
                return;

            case 0x7F:
                return;

            case >= 0x40 and <= 0x7E:
                Dispatch(sink, b);
                return;

            default:
                State = VtState.CsiIgnore;
                return;
        }
    }

    private void CsiParam(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        switch (b)
        {
            case >= 0x30 and <= 0x39:
                Digit(b);
                return;

            case 0x3A:
                SubParamSeparator();
                return;

            case 0x3B:
                ParamSeparator();
                return;

            case >= 0x20 and <= 0x2F:
                Collect(b);
                State = VtState.CsiIntermediate;
                return;

            case >= 0x3C and <= 0x3F:
                // A private marker after parameters have started is out of order.
                State = VtState.CsiIgnore;
                return;

            case 0x7F:
                return;

            case >= 0x40 and <= 0x7E:
                Dispatch(sink, b);
                return;

            default:
                State = VtState.CsiIgnore;
                return;
        }
    }

    private void CsiIntermediate(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                return;

            case >= 0x30 and <= 0x3F:
                // Parameters after intermediates are out of order.
                State = VtState.CsiIgnore;
                return;

            case 0x7F:
                return;

            case >= 0x40 and <= 0x7E:
                Dispatch(sink, b);
                return;

            default:
                State = VtState.CsiIgnore;
                return;
        }
    }

    private void CsiIgnore(byte b, IVtEventSink sink)
    {
        if (IsC0(b))
        {
            sink.Execute(b);
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            // The sequence has ended. Nothing is dispatched: a sequence this parser
            // could not read is one it must not guess at.
            State = VtState.Ground;
        }
    }

    private void DcsEntry(byte b, IVtEventSink sink)
    {
        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                State = VtState.DcsIntermediate;
                return;

            case >= 0x30 and <= 0x39:
                Digit(b);
                State = VtState.DcsParam;
                return;

            case 0x3A:
                SubParamSeparator();
                State = VtState.DcsParam;
                return;

            case 0x3B:
                ParamSeparator();
                State = VtState.DcsParam;
                return;

            case >= 0x3C and <= 0x3F:
                Collect(b);
                State = VtState.DcsParam;
                return;

            case >= 0x40 and <= 0x7E:
                Hook(sink, b);
                return;

            default:
                return;
        }
    }

    private void DcsParam(byte b, IVtEventSink sink)
    {
        switch (b)
        {
            case >= 0x30 and <= 0x39:
                Digit(b);
                return;

            case 0x3A:
                SubParamSeparator();
                return;

            case 0x3B:
                ParamSeparator();
                return;

            case >= 0x20 and <= 0x2F:
                Collect(b);
                State = VtState.DcsIntermediate;
                return;

            case >= 0x3C and <= 0x3F:
                State = VtState.DcsIgnore;
                return;

            case >= 0x40 and <= 0x7E:
                Hook(sink, b);
                return;

            default:
                return;
        }
    }

    private void DcsIntermediate(byte b, IVtEventSink sink)
    {
        switch (b)
        {
            case >= 0x20 and <= 0x2F:
                Collect(b);
                return;

            case >= 0x30 and <= 0x3F:
                State = VtState.DcsIgnore;
                return;

            case >= 0x40 and <= 0x7E:
                Hook(sink, b);
                return;

            default:
                return;
        }
    }

    private void DcsPassthrough(byte b, IVtEventSink sink)
    {
        if (b is 0x7F or (>= 0x00 and <= 0x17) or 0x19 or (>= 0x1C and <= 0x1F))
        {
            return;
        }

        sink.Put(b);
    }

    private void OscString(byte b, IVtEventSink sink)
    {
        if (b == 0x07)
        {
            // BEL terminates an OSC. Non-standard, universal, and what conhost emits
            // for a title change — so the parser that refused it would miss every title.
            FinishOsc(sink);
            State = VtState.Ground;
            return;
        }

        if (b < 0x20)
        {
            return;
        }

        if (_osc.Count < MaxOscLength)
        {
            _osc.Add(b);
        }
        else
        {
            _oscTruncated = true;
        }
    }

    // Actions.

    /// <summary>Runs a state's exit action, if it has one.</summary>
    private void LeaveState(IVtEventSink sink)
    {
        switch (State)
        {
            case VtState.OscString:
                FinishOsc(sink);
                break;

            case VtState.DcsPassthrough:
                sink.Unhook();
                break;

            default:
                break;
        }
    }

    private void FinishOsc(IVtEventSink sink)
    {
        // A truncated payload is dropped rather than delivered short. Half a title is
        // not a title, and half a base64 blob is worse than none.
        if (!_oscTruncated)
        {
            sink.OscDispatch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_osc));
        }

        _osc.Clear();
        _oscTruncated = false;
    }

    private void Dispatch(IVtEventSink sink, byte final)
    {
        VtParams parameters = BuildParams();
        sink.CsiDispatch(in parameters, Intermediates, final);
        State = VtState.Ground;
    }

    private void Hook(IVtEventSink sink, byte final)
    {
        VtParams parameters = BuildParams();
        sink.Hook(in parameters, Intermediates, final);
        State = VtState.DcsPassthrough;
    }

    private VtParams BuildParams() =>
        new(_values.AsSpan(0, _valueCount), _starts.AsSpan(0, _paramCount));

    private ReadOnlySpan<byte> Intermediates => _intermediates.AsSpan(0, _intermediateCount);

    private void ClearSequence()
    {
        _valueCount = 0;
        _paramCount = 0;
        _intermediateCount = 0;
        _valueOpen = false;
        _paramOpen = false;
    }

    private void Collect(byte b)
    {
        if (_intermediateCount == MaxIntermediates)
        {
            // More intermediates than DEC allows. The sequence cannot be what it claims
            // to be, so it is abandoned rather than dispatched with the excess dropped.
            State = State is VtState.DcsEntry or VtState.DcsParam or VtState.DcsIntermediate
                ? VtState.DcsIgnore
                : VtState.CsiIgnore;
            return;
        }

        _intermediates[_intermediateCount++] = b;
    }

    private void Digit(byte b)
    {
        if (!OpenValue())
        {
            return;
        }

        int value = (_values[_valueCount - 1] * 10) + (b - (byte)'0');
        _values[_valueCount - 1] = value > MaxParamValue ? MaxParamValue : value;
    }

    private void ParamSeparator()
    {
        // Ensures an empty parameter still occupies a slot, so CSI ;5H has the 5 in
        // position one rather than position zero.
        if (!OpenValue())
        {
            return;
        }

        _valueOpen = false;
        _paramOpen = false;
    }

    private void SubParamSeparator()
    {
        if (!OpenValue())
        {
            return;
        }

        _valueOpen = false;
    }

    /// <summary>
    /// Makes sure there is a value slot accepting digits, starting a new parameter if
    /// the previous one was closed by <c>;</c>. Returns false once the sequence has
    /// grown past what can be represented, at which point it is abandoned.
    /// </summary>
    private bool OpenValue()
    {
        if (_valueOpen)
        {
            return true;
        }

        if (_valueCount == MaxValues || (!_paramOpen && _paramCount == MaxParams))
        {
            State = State is VtState.DcsEntry or VtState.DcsParam or VtState.DcsIntermediate
                ? VtState.DcsIgnore
                : VtState.CsiIgnore;
            return false;
        }

        if (!_paramOpen)
        {
            _starts[_paramCount++] = _valueCount;
            _paramOpen = true;
        }

        _values[_valueCount++] = 0;
        _valueOpen = true;
        return true;
    }

    // UTF-8.

    private void BeginUtf8(byte b, IVtEventSink sink)
    {
        switch (b)
        {
            case >= 0xC2 and <= 0xDF:
                _pending = b & 0x1F;
                _pendingNeeded = 1;
                _pendingLowerBound = 0x80;
                break;

            case >= 0xE0 and <= 0xEF:
                _pending = b & 0x0F;
                _pendingNeeded = 2;
                _pendingLowerBound = 0x800;
                break;

            case >= 0xF0 and <= 0xF4:
                _pending = b & 0x07;
                _pendingNeeded = 3;
                _pendingLowerBound = 0x10000;
                break;

            default:
                // A continuation byte with nothing to continue, or one of the lead
                // bytes (0xC0, 0xC1, 0xF5–0xFF) that can only ever encode something
                // overlong or out of range.
                sink.Print(Rune.ReplacementChar);
                return;
        }

        _pendingSeen = 0;
    }

    private void ContinueUtf8(byte b, IVtEventSink sink)
    {
        _pending = (_pending << 6) | (b & 0x3F);
        _pendingSeen++;

        if (_pendingSeen < _pendingNeeded)
        {
            return;
        }

        int codepoint = _pending;
        int lowerBound = _pendingLowerBound;

        _pending = 0;
        _pendingNeeded = 0;
        _pendingSeen = 0;
        _pendingLowerBound = 0;

        // Overlong encodings, surrogate halves, and anything past the last plane are
        // all rejected. Accepting them would let a stream smuggle a codepoint past any
        // filter that only inspected the shortest form.
        bool valid = codepoint >= lowerBound
            && codepoint <= 0x10FFFF
            && codepoint is < 0xD800 or > 0xDFFF;

        sink.Print(valid ? new Rune(codepoint) : Rune.ReplacementChar);
    }

    private void EmitReplacement(IVtEventSink sink)
    {
        _pending = 0;
        _pendingNeeded = 0;
        _pendingSeen = 0;
        _pendingLowerBound = 0;

        sink.Print(Rune.ReplacementChar);
    }

    private static bool IsC0(byte b) =>
        b is (>= 0x00 and <= 0x17) or 0x19 or (>= 0x1C and <= 0x1F);
}
