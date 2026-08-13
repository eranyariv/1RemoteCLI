using System.Text;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// Which character set a graphic byte is interpreted through.
/// </summary>
public enum Charset : byte
{
    /// <summary>Plain ASCII, and the default.</summary>
    Ascii = 0,

    /// <summary>
    /// DEC special graphics: lower-case letters become box-drawing characters. Still in
    /// use — <c>tput</c>-driven programs and anything built on old curses draw their
    /// borders this way rather than with Unicode.
    /// </summary>
    DecSpecialGraphics = 1,
}

/// <summary>
/// The cursor state that <c>DECSC</c> saves and <c>DECRC</c> restores.
/// <para>
/// More than a position, because the sequence is defined to save the rendition and the
/// character sets too. A program that saves the cursor, writes a coloured status line
/// and restores expects its previous colour back; restoring only the position would
/// leave everything it wrote afterwards in the status line's colour.
/// </para>
/// </summary>
public readonly record struct SavedCursor(
    int Row,
    int Column,
    CellAttributes Attributes,
    bool OriginMode,
    Charset G0,
    Charset G1,
    int ActiveCharset);
