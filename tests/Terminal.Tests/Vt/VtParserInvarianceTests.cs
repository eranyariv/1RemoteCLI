using System.Text;

using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Vt;

/// <summary>
/// The two properties the rest of the system leans on: the parser produces the same
/// events regardless of how the bytes were chunked, and it survives anything.
/// <para>
/// These are the tests that justify the parser's design. Everything downstream — the
/// screen model, the framing layer, the resume-after-reconnect path — assumes that a
/// pseudoconsole read boundary and a network frame boundary are invisible. That
/// assumption is easy to state, easy to believe, and impossible to verify by reading
/// the code, so it is verified by running it.
/// </para>
/// </summary>
public class VtParserInvarianceTests
{
    /// <summary>
    /// Real output, or close enough: what a shell prompt, a coding agent's spinner and
    /// a full-screen redraw actually put on the wire.
    /// </summary>
    public static TheoryData<string, string> Corpus =>
        new()
        {
            { "plain text", "hello world\r\n" },
            { "a prompt", "\u001b[32muser\u001b[0m@\u001b[34mhost\u001b[0m:~$ " },
            { "clear and home", "\u001b[2J\u001b[H" },
            { "alternate screen", "\u001b[?1049h\u001b[2J\u001b[Hcontent\u001b[?1049l" },
            { "cursor hide and show", "\u001b[?25lworking\u001b[?25h" },
            { "24-bit colour, semicolons", "\u001b[38;2;255;128;0mwarm\u001b[0m" },
            { "24-bit colour, colons", "\u001b[38:2::255:128:0mwarm\u001b[0m" },
            { "a title change", "\u001b]0;1RemoteCLI\u0007ready" },
            { "a title with the standard terminator", "\u001b]2;title\u001b\\ready" },
            { "bracketed paste", "\u001b[?2004hpasted\u001b[?2004l" },
            { "a device control string", "\u001bP1$r0m\u001b\\" },
            { "unicode", "café → 😀 ✓ ünïcödé" },
            { "box drawing", "┌───┐\r\n│ x │\r\n└───┘\r\n" },
            { "a spinner frame", "\r\u001b[K⠋ thinking…" },
            { "scroll region", "\u001b[1;24r\u001b[24;1H\u001b[1S" },
            { "save and restore", "\u001b7\u001b[10;10H\u001b8" },
            { "reset", "\u001bc" },
            { "charset selection", "\u001b(0lqk\u001b(B" },
            { "erase variants", "\u001b[K\u001b[1K\u001b[2K\u001b[J\u001b[1J" },
            { "a mangled sequence", "\u001b[1;\u001b[2mrecovered" },
            {
                "everything at once",
                "\u001b[?1049h\u001b[2J\u001b[H\u001b[38:2::120:200:120m╭─ 1RemoteCLI ─╮\u001b[0m\r\n"
                + "\u001b[?25l⠙ attaching…\u001b[?25h\r\n\u001b]0;attached\u0007"
                + "\u001b[1;32m✓\u001b[0m done\r\n\u001b[?1049l"
            },
        };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ChunkingCannotChangeWhatTheParserReports(string name, string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        string expected = Describe(ParseWhole(bytes));

        // Every single split point, which covers every boundary a read could land on.
        for (int split = 0; split <= bytes.Length; split++)
        {
            string actual = Describe(ParseChunked(bytes, [split]));
            Assert.True(
                expected == actual,
                $"{name}: splitting at {split} changed the events.\n  whole: {expected}\n  split: {actual}");
        }

        // And one byte at a time, the worst case.
        Assert.Equal(expected, Describe(ParseChunked(bytes, Enumerable.Range(1, bytes.Length).ToArray())));
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void RandomChunkingCannotChangeWhatTheParserReports(string name, string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        string expected = Describe(ParseWhole(bytes));

        // A fixed seed: a property test that cannot be re-run on the failing input is
        // not much use when it fails on a build machine at three in the morning.
        var random = new Random(20260101);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var splits = new List<int>();
            int at = 0;

            while (at < bytes.Length)
            {
                at += random.Next(1, Math.Min(5, bytes.Length - at + 1));
                splits.Add(Math.Min(at, bytes.Length));
            }

            string actual = Describe(ParseChunked(bytes, [.. splits]));
            Assert.True(
                expected == actual,
                $"{name}: splitting at [{string.Join(",", splits)}] changed the events.\n"
                + $"  whole: {expected}\n  split: {actual}");
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void RealSequencesLeaveTheParserSomewhereItCanBeCut(string name, string input)
    {
        var parser = new VtParser();
        parser.Parse(Encoding.UTF8.GetBytes(input), NullVtEventSink.Instance);

        Assert.True(parser.IsAtSafeBoundary, $"{name} left the parser mid-sequence.");
    }

    [Fact]
    public void ArbitraryBytesNeverThrow()
    {
        // A session's output is whatever the user ran, so it is attacker-influenced by
        // definition. One bad byte must not be able to take a session down.
        var random = new Random(20260102);

        for (int attempt = 0; attempt < 500; attempt++)
        {
            var parser = new VtParser();
            var sink = new RecordingSink();
            var bytes = new byte[random.Next(1, 512)];
            random.NextBytes(bytes);

            parser.Parse(bytes, sink);
            parser.Reset();

            Assert.Equal(VtState.Ground, parser.State);
        }
    }

    [Fact]
    public void ArbitraryBytesBiasedTowardsEscapesNeverThrow()
    {
        // Uniform random bytes hit ESC only 1 in 256 of the time, so they barely
        // exercise the state machine. This weights the alphabet towards the bytes that
        // actually drive transitions.
        byte[] alphabet =
        [
            0x1B, 0x1B, 0x1B, 0x5B, 0x5D, 0x50, 0x3B, 0x3A, 0x3F, 0x21,
            0x30, 0x31, 0x39, 0x6D, 0x48, 0x4A, 0x07, 0x18, 0x1A, 0x0A,
            0x0D, 0x20, 0x41, 0x7F, 0x80, 0xC3, 0xE2, 0xF0, 0xFF, 0x5C,
        ];

        var random = new Random(20260103);

        for (int attempt = 0; attempt < 500; attempt++)
        {
            var parser = new VtParser();
            var sink = new RecordingSink();
            var bytes = new byte[random.Next(1, 512)];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = alphabet[random.Next(alphabet.Length)];
            }

            parser.Parse(bytes, sink);

            // Whatever it did, cancel returns it to a known place.
            parser.Parse([0x18], sink);
            Assert.Equal(VtState.Ground, parser.State);
        }
    }

    [Fact]
    public void ArbitraryBytesChunkedArbitrarilyStillAgree()
    {
        var random = new Random(20260104);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var bytes = new byte[random.Next(1, 256)];
            random.NextBytes(bytes);

            var splits = new List<int>();
            int at = 0;

            while (at < bytes.Length)
            {
                at += random.Next(1, Math.Min(8, bytes.Length - at + 1));
                splits.Add(Math.Min(at, bytes.Length));
            }

            Assert.True(
                Describe(ParseWhole(bytes)) == Describe(ParseChunked(bytes, [.. splits])),
                $"chunking changed the events for {Convert.ToHexString(bytes)} "
                + $"split at [{string.Join(",", splits)}]");
        }
    }

    private static List<VtEvent> ParseWhole(byte[] bytes)
    {
        var sink = new RecordingSink();
        new VtParser().Parse(bytes, sink);
        return sink.Events;
    }

    /// <summary>
    /// Feeds <paramref name="bytes"/> to one parser in pieces ending at each of
    /// <paramref name="splits"/>, which is what a series of reads looks like.
    /// </summary>
    private static List<VtEvent> ParseChunked(byte[] bytes, int[] splits)
    {
        var parser = new VtParser();
        var sink = new RecordingSink();
        int at = 0;

        foreach (int split in splits)
        {
            parser.Parse(bytes.AsSpan(at, split - at), sink);
            at = split;
        }

        if (at < bytes.Length)
        {
            parser.Parse(bytes.AsSpan(at), sink);
        }

        return sink.Events;
    }

    private static string Describe(List<VtEvent> events) => string.Join(" ", events);
}
