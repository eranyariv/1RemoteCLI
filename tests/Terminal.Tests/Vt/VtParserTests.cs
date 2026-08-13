using System.Text;

using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Vt;

/// <summary>
/// What the parser does with the sequences programs actually emit, and with the ones
/// they emit by accident.
/// </summary>
public class VtParserTests
{
    private readonly VtParser _parser = new();
    private readonly RecordingSink _sink = new();

    private void Feed(string ascii) => _parser.Parse(Encoding.UTF8.GetBytes(ascii), _sink);

    private void Feed(params byte[] bytes) => _parser.Parse(bytes, _sink);

    // Printing.

    [Fact]
    public void PlainTextIsPrintedCharacterByCharacter()
    {
        Feed("hello");

        Assert.Equal("hello", _sink.Text);
        Assert.Equal(5, _sink.Events.Count);
    }

    [Fact]
    public void ControlCharactersAreReportedSeparatelyFromText()
    {
        Feed("a\r\nb");

        Assert.Equal(
        [
            new PrintEvent(new Rune('a')),
            new ExecuteEvent(0x0D),
            new ExecuteEvent(0x0A),
            new PrintEvent(new Rune('b')),
        ],
            _sink.Events);
    }

    [Fact]
    public void TheEscapeByteItselfIsNeverPrinted()
    {
        Feed("\u001b[0m");

        Assert.Equal(string.Empty, _sink.Text);
    }

    // CSI.

    [Fact]
    public void ASequenceWithNoParametersReportsNone()
    {
        Feed("\u001b[m");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Empty(csi.Parameters);
        Assert.Equal((byte)'m', csi.Final);
    }

    [Fact]
    public void AnOmittedParameterStillHoldsItsPlace()
    {
        // CSI ;5H means row default, column 5. If the empty parameter collapsed, the 5
        // would land in the row slot and the cursor would go to the wrong place.
        Feed("\u001b[;5H");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[0], [5]], csi.Parameters);
    }

    [Fact]
    public void SemicolonsSeparateParameters()
    {
        Feed("\u001b[1;2m");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[1], [2]], csi.Parameters);
    }

    [Fact]
    public void ColonsKeepSubParametersTogether()
    {
        // The colour form modern programs emit. Flattened, this is indistinguishable
        // from five unrelated attributes.
        Feed("\u001b[38:2::255:0:0m");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[38, 2, 0, 255, 0, 0]], csi.Parameters);
    }

    [Fact]
    public void TheOlderSemicolonColourFormStaysDistinctFromTheColonOne()
    {
        Feed("\u001b[38;2;255;0;0m");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[38], [2], [255], [0], [0]], csi.Parameters);
    }

    [Fact]
    public void APrivateMarkerIsKeptBecauseItChangesWhatTheFinalByteMeans()
    {
        // CSI ? 25 h hides the cursor; CSI 25 h is a different, unrelated mode.
        Feed("\u001b[?25h");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[25]], csi.Parameters);
        Assert.Equal([(byte)'?'], csi.Intermediates);
        Assert.Equal((byte)'h', csi.Final);
    }

    [Fact]
    public void AnIntermediateByteIsKept()
    {
        Feed("\u001b[!p");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([(byte)'!'], csi.Intermediates);
        Assert.Equal((byte)'p', csi.Final);
    }

    [Fact]
    public void ControlCharactersInsideASequenceStillRun()
    {
        // A carriage return arriving mid-sequence is executed immediately and the
        // sequence carries on around it, which is what real terminals do.
        Feed("\u001b[1;\r2m");

        Assert.Equal(
        [
            new ExecuteEvent(0x0D),
            new CsiEvent([[1], [2]], [], (byte)'m'),
        ],
            _sink.Events);
    }

    [Fact]
    public void AHugeParameterIsClampedRatherThanAllowedToOverflow()
    {
        Feed("\u001b[99999999999999H");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[65535]], csi.Parameters);
    }

    [Fact]
    public void ASequenceWithAbsurdlyManyParametersIsAbandonedRatherThanTruncated()
    {
        // Dispatching the first 32 of 100 parameters would be a quietly wrong command.
        // Dropping it is the only honest option.
        Feed("\u001b[" + string.Join(";", Enumerable.Repeat("1", 100)) + "m");

        Assert.Empty(_sink.Events);
        Assert.Equal(VtState.Ground, _parser.State);
    }

    [Fact]
    public void ASequenceWithTooManyIntermediatesIsAbandoned()
    {
        Feed("\u001b[!\"#p");

        Assert.Empty(_sink.Events);
        Assert.Equal(VtState.Ground, _parser.State);
    }

    [Fact]
    public void TextAfterAnAbandonedSequenceStillPrints()
    {
        Feed("\u001b[!\"#pok");

        Assert.Equal("ok", _sink.Text);
    }

    // ESC.

    [Fact]
    public void ATwoByteEscapeSequenceDispatches()
    {
        Feed("\u001b7");

        EscEvent esc = Assert.IsType<EscEvent>(Assert.Single(_sink.Events));
        Assert.Equal((byte)'7', esc.Final);
        Assert.Empty(esc.Intermediates);
    }

    [Fact]
    public void AnEscapeSequenceWithAnIntermediateDispatches()
    {
        Feed("\u001b(B");

        EscEvent esc = Assert.IsType<EscEvent>(Assert.Single(_sink.Events));
        Assert.Equal([(byte)'('], esc.Intermediates);
        Assert.Equal((byte)'B', esc.Final);
    }

    [Fact]
    public void AnEscapeInTheMiddleOfASequenceStartsOver()
    {
        // Programs cancel a half-written sequence this way, and so does a stream that
        // was cut and resumed.
        Feed("\u001b[12\u001b[3m");

        CsiEvent csi = Assert.IsType<CsiEvent>(Assert.Single(_sink.Events));
        Assert.Equal([[3]], csi.Parameters);
    }

    [Fact]
    public void CancelAbandonsWhateverWasInProgress()
    {
        Feed("\u001b[12\u0018ok");

        Assert.Equal(
        [
            new ExecuteEvent(0x18),
            new PrintEvent(new Rune('o')),
            new PrintEvent(new Rune('k')),
        ],
            _sink.Events);
    }

    // OSC.

    [Fact]
    public void ATitleTerminatedByBellIsDelivered()
    {
        // What conhost emits. A parser that only accepted the standard terminator would
        // miss every title change on Windows.
        Feed("\u001b]0;my title\u0007");

        OscEvent osc = Assert.IsType<OscEvent>(Assert.Single(_sink.Events));
        Assert.Equal("0;my title", osc.Text);
    }

    [Fact]
    public void ATitleTerminatedByTheStandardStringTerminatorIsDelivered()
    {
        Feed("\u001b]0;my title\u001b\\");

        Assert.Equal(
        [
            new OscEvent(Encoding.UTF8.GetBytes("0;my title")),
            new EscEvent([], (byte)'\\'),
        ],
            _sink.Events);
    }

    [Fact]
    public void AnOscPayloadIsHandedOverUndecoded()
    {
        // OSC 52 carries base64, not text. Decoding here would be guessing.
        Feed("\u001b]52;c;aGVsbG8=\u0007");

        OscEvent osc = Assert.IsType<OscEvent>(Assert.Single(_sink.Events));
        Assert.Equal("52;c;aGVsbG8="u8.ToArray(), osc.Data);
    }

    [Fact]
    public void AnUnboundedOscPayloadIsDroppedRatherThanBuffered()
    {
        Feed("\u001b]0;" + new string('x', 20_000) + "\u0007");

        Assert.Empty(_sink.Events);
        Assert.Equal(VtState.Ground, _parser.State);
    }

    [Fact]
    public void TextAfterAnOversizedOscStillPrints()
    {
        Feed("\u001b]0;" + new string('x', 20_000) + "\u0007ok");

        Assert.Equal("ok", _sink.Text);
    }

    // DCS.

    [Fact]
    public void ADeviceControlStringIsReportedAsHookPayloadUnhook()
    {
        Feed("\u001bP1$rab\u001b\\");

        Assert.Equal(
        [
            new HookEvent([[1]], [(byte)'$'], (byte)'r'),
            new PutEvent((byte)'a'),
            new PutEvent((byte)'b'),
            new UnhookEvent(),
            new EscEvent([], (byte)'\\'),
        ],
            _sink.Events);
    }

    // UTF-8.

    [Fact]
    public void MultiByteCharactersDecodeToASingleRune()
    {
        Feed("é→😀");

        Assert.Equal("é→😀", _sink.Text);
        Assert.Equal(3, _sink.Events.Count);
    }

    [Fact]
    public void ACharacterSplitAcrossTwoCallsStillDecodes()
    {
        // The case that motivated a byte-oriented parser: a 4 KB read from a
        // pseudoconsole lands wherever it lands.
        byte[] bytes = Encoding.UTF8.GetBytes("é");

        _parser.Parse(bytes.AsSpan(0, 1), _sink);
        Assert.Empty(_sink.Events);

        _parser.Parse(bytes.AsSpan(1), _sink);
        Assert.Equal("é", _sink.Text);
    }

    [Fact]
    public void AnIncompleteCharacterMeansTheStreamCannotBeCutHere()
    {
        _parser.Parse(Encoding.UTF8.GetBytes("é").AsSpan(0, 1), _sink);

        Assert.Equal(VtState.Ground, _parser.State);
        Assert.False(_parser.IsAtSafeBoundary);
    }

    [Fact]
    public void AHalfWrittenSequenceMeansTheStreamCannotBeCutHere()
    {
        Feed("\u001b[1");

        Assert.False(_parser.IsAtSafeBoundary);
    }

    [Fact]
    public void AfterACompleteSequenceTheStreamCanBeCut()
    {
        Feed("\u001b[1m");

        Assert.True(_parser.IsAtSafeBoundary);
    }

    [Theory]
    [InlineData(new byte[] { 0xC3 })]                   // truncated two-byte lead
    [InlineData(new byte[] { 0x80 })]                   // continuation with nothing to continue
    [InlineData(new byte[] { 0xC0, 0x80 })]             // overlong null
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 })]       // overlong, three bytes
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]       // surrogate half
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })] // past the last plane
    [InlineData(new byte[] { 0xFF })]                   // never valid
    public void InvalidUtf8BecomesTheReplacementCharacter(byte[] bytes)
    {
        Feed(bytes);
        Feed("\u001b[m"); // flush anything the parser was still holding

        Assert.Contains(Rune.ReplacementChar.ToString(), _sink.Text);
        Assert.DoesNotContain("\u0000", _sink.Text);
    }

    [Fact]
    public void AByteThatCutsACharacterShortIsStillProcessed()
    {
        // "é" abandoned halfway, then a newline. The newline must survive, because
        // dropping it would lose a line of output.
        Feed(0xC3, 0x0A);

        Assert.Equal(
        [
            new PrintEvent(Rune.ReplacementChar),
            new ExecuteEvent(0x0A),
        ],
            _sink.Events);
    }

    [Fact]
    public void AnEscapeThatCutsACharacterShortStartsTheSequenceAnyway()
    {
        Feed([0xC3, .. Encoding.UTF8.GetBytes("\u001b[1m")]);

        Assert.Equal(
        [
            new PrintEvent(Rune.ReplacementChar),
            new CsiEvent([[1]], [], (byte)'m'),
        ],
            _sink.Events);
    }

    [Fact]
    public void HighBytesInsideASequenceAreSwallowedRatherThanPrinted()
    {
        // Letting them through would paint mojibake in the middle of the screen.
        Feed([0x1B, 0x5B, 0xC3, 0xA9, (byte)'m', (byte)'o', (byte)'k']);

        Assert.Equal("ok", _sink.Text);
    }

    // Housekeeping.

    [Fact]
    public void ResetAbandonsASequenceInProgress()
    {
        Feed("\u001b[12");
        _parser.Reset();

        Assert.Equal(VtState.Ground, _parser.State);
        Assert.True(_parser.IsAtSafeBoundary);

        Feed("ok");
        Assert.Equal("ok", _sink.Text);
    }

    [Fact]
    public void ResetDiscardsAPartialCharacter()
    {
        Feed(0xC3);
        _parser.Reset();

        Assert.True(_parser.IsAtSafeBoundary);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void TheSinkIsRequired() =>
        Assert.Throws<ArgumentNullException>(() => _parser.Parse("x"u8, null!));
}
