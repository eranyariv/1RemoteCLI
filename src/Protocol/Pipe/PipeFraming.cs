using System.Buffers.Binary;
using MessagePack;

namespace OneRemoteCli.Protocol.Pipe;

/// <summary>
/// Length-prefixed MessagePack framing for the wrapper-to-agent named pipe.
/// <para>
/// A named pipe in byte mode gives no message boundaries, so every frame is written
/// as a 4-byte little-endian length followed by that many payload bytes. Reads are
/// exact-length so a partially delivered frame blocks rather than being mistaken for
/// a short one.
/// </para>
/// </summary>
public static class PipeFraming
{
    /// <summary>
    /// Largest frame accepted. Terminal output is coalesced well below this; anything
    /// larger means a desynchronised or hostile stream and is rejected rather than
    /// used to allocate an arbitrary buffer.
    /// </summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>Wraps <paramref name="message"/> in an envelope and writes one frame.</summary>
    public static ValueTask WriteAsync<T>(
        Stream stream,
        PipeMessageKind kind,
        T message,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, Encode(kind, message), cancellationToken);

    /// <summary>
    /// Packs a message into an envelope without writing it.
    /// <para>
    /// Separate from <see cref="WriteAsync{T}"/> so a sender can encode while it still
    /// knows the concrete message type and queue the result. Deferring serialisation
    /// to the point of writing would mean queueing values as <c>object</c>, which
    /// MessagePack cannot serialise without falling back to embedding type names.
    /// </para>
    /// </summary>
    public static PipeEnvelope Encode<T>(PipeMessageKind kind, T message) =>
        new()
        {
            Kind = kind,
            Payload = MessagePackSerializer.Serialize(message, Options),
        };

    /// <summary>Writes one already-encoded frame.</summary>
    public static async ValueTask WriteAsync(
        Stream stream,
        PipeEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        byte[] body = MessagePackSerializer.Serialize(envelope, Options, cancellationToken);
        if (body.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Frame of {body.Length} bytes exceeds the {MaxFrameBytes} byte limit.");
        }

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Returns null on a clean end of stream, which is how the agent
    /// learns a wrapper process went away.
    /// </summary>
    public static async ValueTask<PipeEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }

        byte[] body = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Stream ended part-way through a frame.");
        }

        return MessagePackSerializer.Deserialize<PipeEnvelope>(body, Options, cancellationToken);
    }

    /// <summary>Decodes an envelope payload into its concrete message type.</summary>
    public static T DecodePayload<T>(PipeEnvelope envelope) =>
        MessagePackSerializer.Deserialize<T>(envelope.Payload, Options);

    /// <summary>
    /// Fills <paramref name="buffer"/> completely. Returns false only when the stream
    /// ended before a single byte was read, which is a clean disconnect rather than a
    /// truncated frame.
    /// </summary>
    private static async ValueTask<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read != 0
                    ? throw new EndOfStreamException("Stream ended part-way through a frame.")
                    : false;
            }

            read += n;
        }

        return true;
    }
}
