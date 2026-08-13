using System.Text;

using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Tests.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Vt;

/// <summary>
/// A snapshot is produced in one piece and may be far larger than one message. Cutting
/// it is safe only where a terminal is between sequences, and the only convincing proof
/// of that is to cut a real screen apart and put it back together.
/// </summary>
public sealed class VtChunkerTests
{
    [Fact]
    public void ThePiecesAreExactlyTheOriginal()
    {
        byte[] input = Encoding.UTF8.GetBytes(BusyScreenStream(rows: 24, columns: 80));

        IReadOnlyList<byte[]> chunks = VtChunker.Split(input, 512);

        Assert.Equal(input, chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public void NoPieceExceedsTheLimit()
    {
        byte[] input = Encoding.UTF8.GetBytes(BusyScreenStream(rows: 24, columns: 80));

        foreach (byte[] chunk in VtChunker.Split(input, 300))
        {
            Assert.True(chunk.Length <= 300, $"a chunk of {chunk.Length} bytes exceeded the limit");
        }
    }

    [Fact]
    public void NoPieceEndsPartwayThroughASequence()
    {
        byte[] input = Encoding.UTF8.GetBytes(BusyScreenStream(rows: 24, columns: 80));

        foreach (byte[] chunk in VtChunker.Split(input, 200))
        {
            var parser = new VtParser();
            parser.Parse(chunk, NullVtEventSink.Instance);

            // This is the property the whole class exists for: a client's parser has to
            // be able to start the next frame from a standing start.
            Assert.True(parser.IsAtSafeBoundary, "a chunk ended inside a sequence");
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(200)]
    [InlineData(1_000)]
    [InlineData(24 * 1024)]
    public void AScreenSurvivesBeingCutUpAndReplayed(int limit)
    {
        var original = new TerminalScreen(24, 80);
        new VtParser().Parse(Encoding.UTF8.GetBytes(BusyScreenStream(rows: 24, columns: 80)), original);

        byte[] snapshot = VtSnapshotWriter.Serialize(original);

        var replayed = new TerminalScreen(24, 80);
        var parser = new VtParser();

        foreach (byte[] chunk in VtChunker.Split(snapshot, limit))
        {
            // Fed one at a time, exactly as the frames arrive at a phone.
            parser.Parse(chunk, replayed);
        }

        Assert.Equal(ScreenState.Describe(original), ScreenState.Describe(replayed));
    }

    [Fact]
    public void ADenseTruecolourScreenIsCutIntoSeveralFramesRatherThanOneOversizedOne()
    {
        // The case that motivated this: a full screen where every cell changes colour
        // serializes to far more than a single message may carry.
        var screen = new TerminalScreen(60, 200);
        var stream = new StringBuilder();

        for (int row = 0; row < 60; row++)
        {
            for (int column = 0; column < 200; column++)
            {
                stream.Append("\u001b[38;2;")
                    .Append((row * 7) % 256).Append(';')
                    .Append((column * 11) % 256).Append(';')
                    .Append((row + column) % 256)
                    .Append('m')
                    .Append((char)('a' + ((row + column) % 26)));
            }
        }

        new VtParser().Parse(Encoding.UTF8.GetBytes(stream.ToString()), screen);

        byte[] snapshot = VtSnapshotWriter.Serialize(screen);
        IReadOnlyList<byte[]> chunks = VtChunker.Split(snapshot, 24 * 1024);

        Assert.True(snapshot.Length > 24 * 1024, $"the snapshot was only {snapshot.Length} bytes");
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 24 * 1024));
    }

    [Fact]
    public void SomethingIsAlwaysReturnedEvenForNothing()
    {
        // A caller sending a snapshot must always have a frame to send, because the
        // frame is what tells the client to clear the screen it is holding.
        IReadOnlyList<byte[]> chunks = VtChunker.Split([], 1_024);

        Assert.Single(chunks);
        Assert.Empty(chunks[0]);
    }

    [Fact]
    public void ASequenceLongerThanTheLimitIsCutRatherThanHeldForever()
    {
        // Nothing real emits this. The point is that the chunker terminates on it
        // instead of looping on a boundary that will never arrive.
        byte[] input = Encoding.UTF8.GetBytes("\u001b[" + new string('1', 500) + "m");

        IReadOnlyList<byte[]> chunks = VtChunker.Split(input, 64);

        Assert.Equal(input, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 64));
    }

    /// <summary>Colour, cursor moves, wrapping text and a scroll region: the shape of a real repaint.</summary>
    private static string BusyScreenStream(int rows, int columns)
    {
        var stream = new StringBuilder();

        stream.Append("\u001b[?1049h");
        stream.Append("\u001b[2J");
        stream.Append("\u001b]0;a title long enough to matter\u0007");
        stream.Append("\u001b[3;20r");

        for (int row = 1; row <= rows; row++)
        {
            stream.Append("\u001b[").Append(row).Append(";1H");
            stream.Append("\u001b[").Append(31 + (row % 7)).Append('m');
            stream.Append("\u001b[1m");

            for (int column = 0; column < columns - 2; column++)
            {
                stream.Append((char)('A' + ((row + column) % 26)));
            }

            stream.Append("\u001b[0m");
        }

        stream.Append("\u001b[7;7H");

        return stream.ToString();
    }
}
