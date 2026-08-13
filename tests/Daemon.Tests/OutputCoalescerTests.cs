using System.Text;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The rules that keep a flood from making the app unusable, and keep a frame from
/// being a lie.
/// </summary>
public sealed class OutputCoalescerTests
{
    /// <summary>
    /// Many small writes become one frame. This is the entire point: a program that
    /// redraws two hundred times a second must cost one message, not two hundred.
    /// </summary>
    [Fact]
    public void ManyWritesBecomeOneFrame()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        for (int i = 0; i < 200; i++)
        {
            feeder.Append(coalescer, "x");
        }

        Assert.True(coalescer.TryTake(out byte[] frame));
        Assert.Equal(200, frame.Length);
        Assert.False(coalescer.TryTake(out _));
    }

    /// <summary>
    /// No frame exceeds the cap, because SignalR refuses one that does — and a refused
    /// message is not a slow screen, it is a dead connection.
    /// </summary>
    [Fact]
    public void NoFrameExceedsTheCap()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        feeder.Append(coalescer, new string('a', OutputCoalescer.MaxFrameBytes * 3));

        int frames = 0;
        int total = 0;

        while (coalescer.TryTake(out byte[] frame))
        {
            Assert.InRange(frame.Length, 1, OutputCoalescer.MaxFrameBytes);
            frames++;
            total += frame.Length;
        }

        Assert.Equal(OutputCoalescer.MaxFrameBytes * 3, total);
        Assert.Equal(3, frames);
    }

    /// <summary>
    /// Output that ends partway through an escape sequence is held back.
    /// <para>
    /// Sending it would not show a partial picture but a wrong one: the client's
    /// parser would read whatever arrived next as the sequence's parameters.
    /// </para>
    /// </summary>
    [Fact]
    public void APartialSequenceIsHeldUntilItIsFinished()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        // "hello" is complete; the CSI that follows it is not.
        feeder.Append(coalescer, "hello\u001b[3");

        Assert.True(coalescer.TryTake(out byte[] first));
        Assert.Equal("hello", Encoding.UTF8.GetString(first));

        feeder.Append(coalescer, "1m");

        Assert.True(coalescer.TryTake(out byte[] second));
        Assert.Equal("\u001b[31m", Encoding.UTF8.GetString(second));
    }

    /// <summary>
    /// A character split across two reads is not split across two frames. A frame
    /// ending between the two bytes of "é" is exactly as broken as one ending between
    /// ESC and '[', and only the parser knows the difference.
    /// </summary>
    [Fact]
    public void AMultiByteCharacterIsNotCutInHalf()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        byte[] utf8 = Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, utf8.Length);

        feeder.Append(coalescer, utf8.AsSpan(0, 1));
        Assert.False(coalescer.TryTake(out _));

        feeder.Append(coalescer, utf8.AsSpan(1, 1));

        Assert.True(coalescer.TryTake(out byte[] frame));
        Assert.Equal("é", Encoding.UTF8.GetString(frame));
    }

    /// <summary>
    /// Nothing is held forever. A stream that never returns to a safe boundary is
    /// malformed, and past some point holding more of it only costs the user memory —
    /// the client's parser is built to resynchronise, so cutting is the lesser harm.
    /// </summary>
    [Fact]
    public void AStreamThatNeverBecomesSafeIsEventuallyCutAnyway()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        // An OSC that is opened and never terminated: the parser stays out of ground
        // for as long as the payload keeps arriving.
        feeder.Append(coalescer, "\u001b]0;");

        string filler = new('t', 4096);

        while (coalescer.Pending < OutputCoalescer.MaxFrameBytes * 10)
        {
            Assert.False(coalescer.TryTake(out _));
            feeder.Append(coalescer, filler);
        }

        Assert.True(coalescer.TryTake(out byte[] frame));
        Assert.Equal(OutputCoalescer.MaxFrameBytes, frame.Length);
    }

    /// <summary>
    /// Discarding leaves nothing behind. Used when a snapshot supersedes what is
    /// buffered; a leftover byte would be drawn on top of a screen that already has it.
    /// </summary>
    [Fact]
    public void DiscardingLeavesNothing()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();

        feeder.Append(coalescer, "buffered");
        coalescer.Discard();

        Assert.False(coalescer.TryTake(out _));
        Assert.Equal(0, coalescer.Pending);
    }

    /// <summary>
    /// Frames concatenate back into the original stream.
    /// <para>
    /// The property that actually matters. Every other rule here is about *where* the
    /// cuts fall; this one is about the cuts not losing or duplicating anything, which
    /// is what the user would experience as corruption.
    /// </para>
    /// </summary>
    [Fact]
    public void FramesReassembleIntoExactlyWhatWasWritten()
    {
        OutputCoalescer coalescer = new();
        Feeder feeder = new();
        Random random = new(20260204);

        List<byte> written = [];
        List<byte> sent = [];

        string[] fragments =
        [
            "plain text ", "\u001b[31m", "\u001b[2J", "\u001b[10;20H", "é", "→", "\u001b]0;title\u0007",
            "\r\n", new string('z', 900),
        ];

        for (int i = 0; i < 4000; i++)
        {
            string fragment = fragments[random.Next(fragments.Length)];
            byte[] bytes = Encoding.UTF8.GetBytes(fragment);

            // Split at a random point, so cuts land inside sequences and inside
            // characters — which is what a real pseudoconsole read does.
            int split = random.Next(bytes.Length + 1);
            feeder.Append(coalescer, bytes.AsSpan(0, split));
            feeder.Append(coalescer, bytes.AsSpan(split));

            written.AddRange(bytes);

            while (coalescer.TryTake(out byte[] frame))
            {
                sent.AddRange(frame);
            }
        }

        // Whatever is held back at the end is a genuinely unfinished sequence.
        Assert.Equal(written.Take(sent.Count), sent);
        Assert.True(written.Count - sent.Count < 64, $"{written.Count - sent.Count} bytes left unsent.");
    }

    /// <summary>
    /// Feeds an emulator the way the live path does and hands the coalescer the
    /// boundary that parser reports, so these tests exercise the real rule rather than
    /// a test-only opinion about where a sequence ends.
    /// </summary>
    private sealed class Feeder
    {
        private readonly VtParser _parser = new();
        private readonly TerminalScreen _screen = new(25, 80);

        public void Append(OutputCoalescer coalescer, string text) =>
            Append(coalescer, Encoding.UTF8.GetBytes(text));

        public void Append(OutputCoalescer coalescer, ReadOnlySpan<byte> bytes)
        {
            _parser.Parse(bytes, _screen, out int lastSafeOffset);
            coalescer.Append(bytes, lastSafeOffset);
        }
    }
}
