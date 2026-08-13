namespace OneRemoteCli.Terminal.Vt;

/// <summary>
/// Splits a finished VT stream into pieces small enough to send, cutting only where a
/// terminal could be interrupted without harm.
/// <para>
/// Live output is framed as it arrives, by the parser that is already consuming it. A
/// snapshot is different: it is produced all at once, in one buffer, and can be far
/// larger than a single message may be — a densely coloured full screen runs to tens of
/// kilobytes. It still cannot be cut arbitrarily, because a cut through the middle of an
/// escape sequence leaves the client's parser treating the next frame's text as
/// parameters, and the screen fills with the wrong thing.
/// </para>
/// </summary>
public static class VtChunker
{
    /// <summary>
    /// Cuts <paramref name="bytes"/> into pieces of at most <paramref name="maxChunk"/>,
    /// each ending where the parser is between sequences.
    /// <para>
    /// Always returns at least one piece, so a caller that must send something — a
    /// snapshot the client resets on — always has something to send.
    /// </para>
    /// <para>
    /// A single sequence longer than <paramref name="maxChunk"/> cannot be honoured and
    /// is cut anyway. Nothing a terminal emits comes close, and the alternative is a
    /// frame that grows without limit.
    /// </para>
    /// </summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> bytes, int maxChunk)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunk, 1);

        var chunks = new List<byte[]>();

        if (bytes.IsEmpty)
        {
            chunks.Add([]);
            return chunks;
        }

        int position = 0;

        while (position < bytes.Length)
        {
            int window = Math.Min(maxChunk, bytes.Length - position);

            if (window == bytes.Length - position)
            {
                // The rest fits. A complete stream ends between sequences by
                // construction, so there is nothing to look for.
                chunks.Add(bytes.Slice(position, window).ToArray());
                break;
            }

            // A fresh parser is correct here because every cut lands on a boundary,
            // which is where a parser starts.
            var parser = new VtParser();
            parser.Parse(bytes.Slice(position, window), NullVtEventSink.Instance, out int lastSafeOffset);

            int take = lastSafeOffset > 0 ? lastSafeOffset : window;

            chunks.Add(bytes.Slice(position, take).ToArray());
            position += take;
        }

        return chunks;
    }
}
