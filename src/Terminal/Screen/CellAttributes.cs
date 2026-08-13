namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// The on/off attributes of a cell.
/// <para>
/// The wide-character flags live here rather than beside the character because the grid
/// is what has to stay consistent: a double-width character occupies two cells, and
/// anything that overwrites, erases or scrolls has to be able to see that from the cell
/// alone. Storing the fact on the character would leave the trailing cell looking empty
/// and let half a character survive an overwrite.
/// </para>
/// </summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Reverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,

    /// <summary>The left half of a double-width character.</summary>
    WideLeading = 1 << 8,

    /// <summary>The right half of a double-width character. Carries no character of its own.</summary>
    WideTrailing = 1 << 9,

    /// <summary>Everything that describes appearance rather than grid structure.</summary>
    Rendition = Bold | Dim | Italic | Underline | Blink | Reverse | Hidden | Strikethrough,

    /// <summary>Everything that describes grid structure rather than appearance.</summary>
    Wide = WideLeading | WideTrailing,
}

/// <summary>The rendition of a cell: colours plus <see cref="CellFlags"/>.</summary>
public readonly struct CellAttributes : IEquatable<CellAttributes>
{
    public CellAttributes(VtColor foreground, VtColor background, CellFlags flags)
    {
        Foreground = foreground;
        Background = background;
        Flags = flags;
    }

    /// <summary>Plain text: no colours set, no attributes on. What SGR 0 produces.</summary>
    public static CellAttributes Default => default;

    public VtColor Foreground { get; }

    public VtColor Background { get; }

    public CellFlags Flags { get; }

    public bool Has(CellFlags flag) => (Flags & flag) != 0;

    public CellAttributes With(CellFlags flags) => new(Foreground, Background, flags);

    public CellAttributes WithForeground(VtColor foreground) => new(foreground, Background, Flags);

    public CellAttributes WithBackground(VtColor background) => new(Foreground, background, Flags);

    public CellAttributes Add(CellFlags flag) => new(Foreground, Background, Flags | flag);

    public CellAttributes Remove(CellFlags flag) => new(Foreground, Background, Flags & ~flag);

    /// <summary>
    /// The appearance only, with the wide-character bookkeeping stripped. Used when
    /// comparing two cells for "can these be emitted as one run", where structure is
    /// irrelevant and only rendition matters.
    /// </summary>
    public CellAttributes Rendition => new(Foreground, Background, Flags & CellFlags.Rendition);

    public bool Equals(CellAttributes other) =>
        Flags == other.Flags
        && Foreground.Equals(other.Foreground)
        && Background.Equals(other.Background);

    public override bool Equals(object? obj) => obj is CellAttributes other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Foreground, Background, Flags);

    public static bool operator ==(CellAttributes left, CellAttributes right) => left.Equals(right);

    public static bool operator !=(CellAttributes left, CellAttributes right) => !left.Equals(right);

    public override string ToString() => $"{Foreground}/{Background}/{Flags}";
}
