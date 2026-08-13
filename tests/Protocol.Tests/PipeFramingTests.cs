using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Protocol.Tests;

public class PipeFramingTests
{
    [Fact]
    public async Task RoundTripsASingleFrame()
    {
        using var stream = new MemoryStream();
        var sent = new SessionOpenedMessage
        {
            Program = "claude",
            Args = ["--resume"],
            Cwd = @"C:\Projects\1RemoteCLI",
            Cols = 120,
            Rows = 30,
            DisplayName = "Claude on primary",
        };

        await PipeFraming.WriteAsync(stream, PipeMessageKind.SessionOpened, sent);
        stream.Position = 0;

        PipeEnvelope? envelope = await PipeFraming.ReadAsync(stream);

        Assert.NotNull(envelope);
        Assert.Equal(PipeMessageKind.SessionOpened, envelope.Kind);

        var received = PipeFraming.DecodePayload<SessionOpenedMessage>(envelope);
        Assert.Equal(sent.Program, received.Program);
        Assert.Equal(sent.Args, received.Args);
        Assert.Equal(sent.Cwd, received.Cwd);
        Assert.Equal(sent.Cols, received.Cols);
        Assert.Equal(sent.Rows, received.Rows);
        Assert.Equal(sent.DisplayName, received.DisplayName);
    }

    [Fact]
    public async Task PreservesFrameBoundariesWhenFramesArePipelined()
    {
        using var stream = new MemoryStream();

        await PipeFraming.WriteAsync(stream, PipeMessageKind.Output, new OutputMessage { Bytes = [0x1b, (byte)'[', (byte)'A'] });
        await PipeFraming.WriteAsync(stream, PipeMessageKind.Resize, new ResizeMessage { Cols = 80, Rows = 24 });
        await PipeFraming.WriteAsync(stream, PipeMessageKind.Interrupt, new InterruptMessage());
        stream.Position = 0;

        PipeEnvelope? first = await PipeFraming.ReadAsync(stream);
        PipeEnvelope? second = await PipeFraming.ReadAsync(stream);
        PipeEnvelope? third = await PipeFraming.ReadAsync(stream);
        PipeEnvelope? end = await PipeFraming.ReadAsync(stream);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'A' }, PipeFraming.DecodePayload<OutputMessage>(first).Bytes);

        var resize = PipeFraming.DecodePayload<ResizeMessage>(second);
        Assert.Equal(80, resize.Cols);
        Assert.Equal(24, resize.Rows);

        Assert.Equal(PipeMessageKind.Interrupt, third.Kind);
        Assert.Null(end);
    }

    [Fact]
    public async Task PreservesArbitraryBinaryOutputExactly()
    {
        // Terminal output is not text. Every byte value must survive the trip,
        // including the escape and NUL bytes that a naive string encoding would eat.
        byte[] payload = new byte[256];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, PipeMessageKind.Output, new OutputMessage { Bytes = payload });
        stream.Position = 0;

        PipeEnvelope? envelope = await PipeFraming.ReadAsync(stream);
        Assert.NotNull(envelope);
        Assert.Equal(payload, PipeFraming.DecodePayload<OutputMessage>(envelope).Bytes);
    }

    [Fact]
    public async Task ReturnsNullOnCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        Assert.Null(await PipeFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ThrowsWhenAFrameIsTruncated()
    {
        using var source = new MemoryStream();
        await PipeFraming.WriteAsync(source, PipeMessageKind.Output, new OutputMessage { Bytes = [1, 2, 3, 4, 5] });

        byte[] truncated = source.ToArray()[..^2];
        using var stream = new MemoryStream(truncated);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await PipeFraming.ReadAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PipeFraming.MaxFrameBytes + 1)]
    public async Task RejectsAnOutOfRangeLengthPrefixWithoutAllocating(int length)
    {
        byte[] prefix = BitConverter.GetBytes(length);
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await PipeFraming.ReadAsync(stream));
    }
}
