using System.Text;

using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Screen;

/// <summary>
/// The property that makes re-serialization tractable: whatever a session's screen looks
/// like, writing it out and reading it back into a fresh terminal must land in exactly
/// the same state.
/// <para>
/// This is asserted rather than eyeballed because the output of the writer is not
/// human-checkable. It bears no resemblance to the bytes the program emitted, and its
/// failure modes are all quiet — a missing reset between two runs, a cursor left one row
/// high, the wrong buffer selected. Every one of those looks plausible in a hexdump and
/// obvious in a round trip.
/// </para>
/// </summary>
public sealed class SnapshotRoundTripTests
{
    /// <summary>
    /// Screens built the way real programs build them. Each entry is fed to a screen,
    /// which is then required to survive a round trip.
    /// </summary>
    public static TheoryData<string, string> Corpus()
    {
        var data = new TheoryData<string, string>
        {
            { "empty", "" },
            { "plain text", "hello world" },
            { "several lines", "one\r\ntwo\r\nthree\r\n" },
            { "full screen of text", string.Concat(Enumerable.Repeat("abcdefghij\r\n", 8)) },
            { "basic colours", "\u001b[31mred\u001b[32mgreen\u001b[0m plain" },
            { "bright colours", "\u001b[91mbright\u001b[0m\u001b[102m back \u001b[0m" },
            { "256 colour", "\u001b[38;5;208m208\u001b[48;5;17m on 17\u001b[0m" },
            { "truecolor", "\u001b[38;2;255;128;0mrgb\u001b[48;2;10;20;30m bg\u001b[0m" },
            { "colon truecolor", "\u001b[38:2::12:34:56mcolon\u001b[0m" },
            { "every attribute", "\u001b[1;2;3;4;5;7;8;9mall\u001b[0m off" },
            { "attributes turned off one at a time", "\u001b[1mb\u001b[22mn\u001b[4mu\u001b[24mn\u001b[7mr\u001b[27mn" },
            {
                "coloured bar of trailing blanks",
                "\u001b[44m\u001b[2K\u001b[0mtext"
            },
            {
                "status line protected by a scroll region",
                "\u001b[2;5r\u001b[3;1Hbody\r\n\u001b[1;1H\u001b[7m status \u001b[0m"
            },
            { "origin mode", "\u001b[2;4r\u001b[?6h\u001b[2;3Hinside" },
            { "cursor parked mid screen", "abc\u001b[3;7H" },
            { "cursor hidden", "prompt\u001b[?25l" },
            { "cursor style and blink", "\u001b[4 q\u001b[?12l" },
            { "window title", "\u001b]0;bash — ~/src\u0007$ " },
            { "title with a semicolon", "\u001b]2;a;b;c\u0007" },
            { "saved cursor", "\u001b[3;3H\u001b[35m\u001b7\u001b[1;1Hhome" },
            { "alternate screen", "shell prompt\u001b[?1049h\u001b[Heditor\r\n\u001b[33msecond line" },
            {
                "alternate screen with a coloured clear",
                "primary\u001b[?1049h\u001b[44m\u001b[2J\u001b[Hpane"
            },
            { "alternate screen then back", "shell\u001b[?1049h\u001b[Halt\u001b[?1049l" },
            { "wide characters", "\u001b[H\u4f60\u597d\u4e16\u754c ok" },
            { "combining marks", "e\u0301 a\u0300 n\u0303" },
            { "emoji", "\U0001f600\U0001f680 done" },
            { "dec box drawing", "\u001b(0lqqqk\u001b(B\r\nplain" },
            { "dec charset left active", "\u001b(0mqqqj" },
            { "shift out to g1", "\u001b)0\u000extqu\u000f plain" },
            { "custom tab stops", "\u001b[3g\u001b[5G\u001bH\u001b[13G\u001bH\u001b[1G\ta\tb" },
            { "autowrap off", "\u001b[?7l" + new string('x', 40) },
            { "insert mode", "abcdef\u001b[1;3H\u001b[4hXY" },
            { "bracketed paste and application keys", "\u001b[?2004h\u001b[?1h\u001b=prompt" },
            { "mouse reporting", "\u001b[?1000h\u001b[?1002h\u001b[?1003h\u001b[?1006h\u001b[?1004h" },
            { "screen alignment test", "\u001b#8" },
            { "erase to end of display", "\u001b[Hfilled\r\nmore\u001b[2;3H\u001b[J" },
            { "reverse index at the top", "\u001b[H\u001bM\u001bMtop" },
            { "scrolled past the bottom", string.Concat(Enumerable.Repeat("line\r\n", 12)) },
            { "reset in the middle", "junk\u001b[31m\u001bcclean" },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ASnapshotReproducesTheScreenItWasTakenOf(string name, string input)
    {
        var original = new ScreenHarness(rows: 8, columns: 24).Feed(input).Screen;

        string snapshot = VtSnapshotWriter.SerializeToString(original);
        var restored = new ScreenHarness(rows: 8, columns: 24).Feed(snapshot).Screen;

        Assert.Equal(ScreenState.Describe(original), ScreenState.Describe(restored));
        Assert.NotEqual(string.Empty, name);
    }

    /// <summary>
    /// A snapshot of a restored screen must equal the snapshot it was restored from.
    /// <para>
    /// Stronger than the round trip alone, and it catches a whole class of bug the round
    /// trip cannot: state the writer drops on the floor entirely. If neither screen has
    /// it, both descriptions agree and the round trip passes while the client silently
    /// loses it on every re-snapshot under backpressure.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void SnapshottingIsIdempotent(string name, string input)
    {
        var original = new ScreenHarness(rows: 8, columns: 24).Feed(input).Screen;

        string first = VtSnapshotWriter.SerializeToString(original);
        var restored = new ScreenHarness(rows: 8, columns: 24).Feed(first).Screen;
        string second = VtSnapshotWriter.SerializeToString(restored);

        Assert.Equal(first, second);
        Assert.NotEqual(string.Empty, name);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ASnapshotSurvivesBeingSplitAcrossWrites(string name, string input)
    {
        var original = new ScreenHarness(rows: 8, columns: 24).Feed(input).Screen;
        byte[] snapshot = VtSnapshotWriter.Serialize(original);

        // The snapshot is framed and sent over a network, so it will arrive in pieces.
        // A writer that emitted something the parser only handles when it is whole would
        // pass every other test here.
        var restored = new TerminalScreen(rows: 8, columns: 24);
        var parser = new VtParser();

        for (int offset = 0; offset < snapshot.Length; offset++)
        {
            parser.Parse(snapshot.AsSpan(offset, 1), restored);
        }

        Assert.Equal(ScreenState.Describe(original), ScreenState.Describe(restored));
        Assert.NotEqual(string.Empty, name);
    }

    /// <summary>
    /// The round trip has to hold for screens nobody thought to write a test for.
    /// <para>
    /// The alphabet is biased towards escape, CSI and the characters that appear in
    /// parameters, because uniformly random bytes would reach a dispatch roughly once
    /// every few hundred bytes and would spend the whole test printing text.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(20260201)]
    [InlineData(20260202)]
    [InlineData(20260203)]
    public void TheRoundTripHoldsForRandomlyDrivenScreens(int seed)
    {
        var random = new Random(seed);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            string input = RandomProgram(random);
            var original = new ScreenHarness(rows: 6, columns: 20).Feed(input).Screen;

            string snapshot = VtSnapshotWriter.SerializeToString(original);
            var restored = new ScreenHarness(rows: 6, columns: 20).Feed(snapshot).Screen;

            string expected = ScreenState.Describe(original);
            string actual = ScreenState.Describe(restored);

            if (expected != actual)
            {
                // The generated input is what makes a failure reproducible, so it goes
                // in the message rather than being left for someone to guess at.
                Assert.Fail(
                    $"Round trip failed on seed {seed}, iteration {iteration}."
                    + $"\ninput:    {Printable(input)}"
                    + $"\nsnapshot: {Printable(snapshot)}"
                    + $"\nexpected:\n{expected}"
                    + $"\nactual:\n{actual}");
            }
        }
    }

    [Fact]
    public void ASnapshotStartsFromAKnownStateWithoutDiscardingScrollback()
    {
        var screen = new ScreenHarness().Feed("\u001b[31mred").Screen;

        string snapshot = VtSnapshotWriter.SerializeToString(screen);

        // Without a reset the snapshot would be a delta against whatever the client
        // happened to be showing, which on a re-attach is the previous session. It must
        // not be RIS, though: that also discards the scrollback, which on a phone is the
        // only copy of everything that has already scrolled off the screen.
        Assert.DoesNotContain("\u001bc", snapshot, StringComparison.Ordinal);
        Assert.Contains("\u001b[2J", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankScreenCostsAlmostNothing()
    {
        var screen = new TerminalScreen(rows: 50, columns: 200);

        // Attaching to an idle session is the common case and happens over cellular.
        // It used to be two bytes; it is now the explicit power-on preamble, most of
        // which is the tab stops -- there is no sequence that restores the default set,
        // so they are cleared and rebuilt one column at a time. Still small enough that
        // the round trip is dominated by latency rather than by this, and a phone-sized
        // screen pays a quarter of it.
        Assert.True(VtSnapshotWriter.Serialize(screen).Length < 512);
    }

    [Fact]
    public void ThePreambleShrinksWithTheScreen()
    {
        // The 200-column budget above is the desktop worst case. What actually goes over
        // the air is a phone, and the tab stops are the only part that scales.
        var phone = new TerminalScreen(rows: 28, columns: 53);

        Assert.True(VtSnapshotWriter.Serialize(phone).Length < 256);
    }

    [Fact]
    public void AFullScreenStaysWithinAReasonableBudget()
    {
        var harness = new ScreenHarness(rows: 50, columns: 200);

        for (int row = 0; row < 50; row++)
        {
            harness.Feed("\u001b[33m" + new string('x', 200));
        }

        int length = VtSnapshotWriter.Serialize(harness.Screen).Length;

        // Ten thousand cells, one byte each, plus positioning and one colour run per
        // row. Anything much beyond that means runs are being re-emitted per cell.
        Assert.InRange(length, 10_000, 12_000);
    }

    [Fact]
    public void BlankGapsAreSkippedRatherThanPainted()
    {
        var harness = new ScreenHarness(rows: 2, columns: 40);
        harness.Feed("\u001b[1;1Ha\u001b[1;40Hb");

        string snapshot = VtSnapshotWriter.SerializeToString(harness.Screen);

        // Thirty-eight spaces would be thirty-eight bytes; a forward move is four.
        Assert.Contains("\u001b[38C", snapshot, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state a re-attaching client is actually in: still showing the previous
    /// screen, with whatever modes, colours, charsets and tab stops the program had set
    /// before the connection dropped.
    /// <para>
    /// This is the state a snapshot has to survive being applied to. It used to be free
    /// -- the leading ESC c flattened everything -- but RIS also discards the client's
    /// scrollback, which is real history the user can still scroll back through. The
    /// writer now clears the screen without discarding it, so every piece of state RIS
    /// used to reset has to be reset explicitly, and this is what proves it is.
    /// </para>
    /// </summary>
    private const string DirtyTerminal =
        "\u001b[?1049h\u001b[44;93;1;4;7mleftover on the alternate screen\r\nmore junk"
        + "\u001b[?1049l\u001b[41;32mprevious session\r\nsecond line\r\nthird line"
        + "\u001b[3;7r\u001b[?6h\u001b[?7l\u001b[4h\u001b[?25l\u001b[?2004h\u001b[?1h\u001b="
        + "\u001b[?1000h\u001b[?1002h\u001b[?1006h\u001b[3g\u001b[4G\u001bH\u001b[11G\u001bH"
        + "\u001b(0\u001b)0\u000e\u001b]0;a stale window title\u0007\u001b[2 q"
        + "\u001b[5;5H\u001b7\u001b[2;2H";

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ASnapshotReproducesTheScreenOnATerminalThatIsNotFresh(string name, string input)
    {
        var original = new ScreenHarness(rows: 8, columns: 24).Feed(input).Screen;

        string snapshot = VtSnapshotWriter.SerializeToString(original);
        var restored = new ScreenHarness(rows: 8, columns: 24).Feed(DirtyTerminal).Feed(snapshot).Screen;

        Assert.Equal(ScreenState.Describe(original), ScreenState.Describe(restored));
        Assert.NotEqual(string.Empty, name);
    }

    /// <summary>
    /// The same property with the dirty state generated rather than chosen: the
    /// hand-written one above can only contain what somebody remembered to put in it.
    /// </summary>
    [Theory]
    [InlineData(20260901)]
    [InlineData(20260902)]
    [InlineData(20260903)]
    public void TheRoundTripHoldsWhenRestoringOverAScreenAlreadyInUse(int seed)
    {
        var random = new Random(seed);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            string input = RandomProgram(random);
            string dirty = SafelyTerminated(RandomProgram(random));

            var original = new ScreenHarness(rows: 6, columns: 20).Feed(input).Screen;
            string snapshot = VtSnapshotWriter.SerializeToString(original);

            // Compared against a fresh restore rather than against the original,
            // because those are two different properties. Whether the writer captures
            // a screen faithfully is what the tests above are for -- and issue #214 is
            // one input where it does not. What matters here is that applying a
            // snapshot does not depend on what the client happened to be showing, so
            // that the reset can stop discarding scrollback.
            var freshly = new ScreenHarness(rows: 6, columns: 20).Feed(snapshot).Screen;
            var restored = new ScreenHarness(rows: 6, columns: 20).Feed(dirty).Feed(snapshot).Screen;

            string expected = ScreenState.Describe(freshly);
            string actual = ScreenState.Describe(restored);

            if (expected != actual)
            {
                Assert.Fail(
                    $"Restoring over a dirty screen differed from restoring onto a fresh one"
                    + $" on seed {seed}, iteration {iteration}."
                    + $"\ninput:    {Printable(input)}"
                    + $"\ndirty:    {Printable(dirty)}"
                    + $"\nsnapshot: {Printable(snapshot)}"
                    + $"\nfresh:\n{expected}"
                    + $"\ndirty:\n{actual}");
            }
        }
    }

    /// <summary>
    /// Cuts a generated program at the last point a frame could have ended.
    /// <para>
    /// The agent never splices a snapshot onto a half-finished escape sequence: output
    /// is framed at the parser's last safe offset, so a client beginning a snapshot is
    /// always between sequences. Without this the generator eventually produces a
    /// program ending mid-OSC, whose string-terminator hunt swallows the snapshot's own
    /// opening bytes -- which tests the framing contract rather than the thing this is
    /// about, and fails for a reason that cannot happen in production.
    /// </para>
    /// </summary>
    private static string SafelyTerminated(string program)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(program);
        new VtParser().Parse(bytes, new TerminalScreen(rows: 6, columns: 20), out int lastSafe);

        return lastSafe <= 0 ? string.Empty : Encoding.UTF8.GetString(bytes, 0, lastSafe);
    }

    private static string RandomProgram(Random random)
    {
        string[] fragments =
        [
            "a", "b", "Z", "0", " ", "\r\n", "\r", "\n", "\t",
            "\u4f60", "\u597d", "e\u0301", "\U0001f600",
            "\u001b[H", "\u001b[2;3H", "\u001b[5;9H", "\u001b[K", "\u001b[J", "\u001b[2J",
            "\u001b[1K", "\u001b[0K", "\u001b[3X", "\u001b[2P", "\u001b[2@",
            "\u001b[L", "\u001b[M", "\u001b[S", "\u001b[T",
            "\u001b[31m", "\u001b[42m", "\u001b[0m", "\u001b[1m", "\u001b[4m", "\u001b[7m",
            "\u001b[22m", "\u001b[24m", "\u001b[27m", "\u001b[39m", "\u001b[49m",
            "\u001b[38;5;99m", "\u001b[48;2;1;2;3m", "\u001b[90m", "\u001b[107m",
            "\u001b[?25l", "\u001b[?25h", "\u001b[?7l", "\u001b[?7h", "\u001b[4h", "\u001b[4l",
            "\u001b[?2004h", "\u001b[?1h", "\u001b=", "\u001b>", "\u001b[?1000h", "\u001b[?1006h",
            "\u001b[?1049h", "\u001b[?1049l", "\u001b[?47h", "\u001b[?47l", "\u001b[?1047h",
            "\u001b7", "\u001b8", "\u001bM", "\u001bD", "\u001bE", "\u001bH",
            "\u001b(0", "\u001b(B", "\u001b)0", "\u000e", "\u000f",
            "\u001b[2;5r", "\u001b[r", "\u001b[?6h", "\u001b[?6l", "\u001b[3g", "\u001b[g",
            "\u001b]0;title\u0007", "\u001b]2;x\u0007", "\u001b[2 q", "\u001b[?12l",
            "\u001b#8", "\u001bc",
        ];

        var program = new StringBuilder();
        int length = random.Next(4, 60);

        for (int i = 0; i < length; i++)
        {
            program.Append(fragments[random.Next(fragments.Length)]);
        }

        return program.ToString();
    }

    private static string Printable(string value)
    {
        var text = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            if (character == '\u001b')
            {
                text.Append("<ESC>");
                continue;
            }

            text.Append(character is < ' ' or '\u007f'
                ? $"<{(int)character:x2}>"
                : character.ToString());
        }

        return text.ToString();
    }
}
