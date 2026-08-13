using System.Text;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// One position on the screen.
/// <para>
/// A struct, and a small one, because there are two full grids of these per session and
/// the per-session memory budget is 2 MB. A class here would cost a pointer per cell
/// plus object overhead and turn a 200×50 screen into a hundred thousand live objects
/// for the garbage collector to walk.
/// </para>
/// <para>
/// <paramref name="Cluster"/> is normally null. It is populated only for the rare cell
/// carrying combining marks — "e" followed by a combining acute, which is one thing on
/// screen but several codepoints. Dropping the marks would be silent corruption of the
/// text; giving them their own cells would shift everything after them one column right.
/// A nullable reference costs a field that is almost always null and nothing else.
/// </para>
/// </summary>
public readonly record struct Cell(Rune Rune, string? Cluster, CellAttributes Attributes)
{
    /// <summary>A space with no attributes. What erasing produces.</summary>
    public static Cell Blank => new(BlankRune, null, CellAttributes.Default);

    internal static Rune BlankRune { get; } = new(' ');

    /// <summary>
    /// An erased cell. It keeps the current background colour but nothing else, which is
    /// the "background colour erase" behaviour every modern terminal implements: clearing
    /// a line inside a coloured panel must leave the panel's colour, not a hole, and
    /// carrying the foreground or the underline across would be visible on the next
    /// character written there.
    /// </summary>
    public static Cell BlankWith(VtColor background) =>
        new(BlankRune, null, new CellAttributes(VtColor.Default, background, CellFlags.None));

    /// <summary>What this cell shows, combining marks included.</summary>
    public string Text => Cluster ?? Rune.ToString();

    /// <summary>True when nothing has been written here since it was last erased.</summary>
    public bool IsBlank => Cluster is null && Rune.Value == ' ';

    public bool IsWideLeading => (Attributes.Flags & CellFlags.WideLeading) != 0;

    public bool IsWideTrailing => (Attributes.Flags & CellFlags.WideTrailing) != 0;

    /// <summary>How many columns this cell accounts for: two for the left half of a wide character.</summary>
    public int Width => IsWideLeading ? 2 : 1;

    /// <summary>This cell with <paramref name="mark"/> appended as a combining mark.</summary>
    public Cell WithCombining(Rune mark)
    {
        // A cap, because a stream can emit combining marks without limit and each one
        // would otherwise grow a string that lives as long as the session.
        const int MaxClusterLength = 16;

        string current = Text;
        return current.Length + mark.Utf16SequenceLength > MaxClusterLength
            ? this
            : this with { Cluster = current + mark.ToString() };
    }

    public override string ToString() => IsWideTrailing ? "\u2510" : Text;
}
