namespace OneRemoteCli.Terminal.Screen;

/// <summary>How a <see cref="VtColor"/> was specified.</summary>
public enum VtColorKind : byte
{
    /// <summary>Whatever the client's terminal uses when nothing is set.</summary>
    Default = 0,

    /// <summary>An index into the 256-colour palette.</summary>
    Indexed = 1,

    /// <summary>A literal 24-bit colour.</summary>
    Rgb = 2,
}

/// <summary>
/// A foreground or background colour.
/// <para>
/// "Default" is kept as a distinct kind rather than resolved to a concrete colour,
/// because the snapshot has to reproduce the screen on someone else's terminal with
/// someone else's theme. Baking in a colour here would mean a dark-themed phone showed
/// a shell that had never set a background as black-on-black.
/// </para>
/// </summary>
public readonly struct VtColor : IEquatable<VtColor>
{
    private VtColor(VtColorKind kind, byte r, byte g, byte b)
    {
        Kind = kind;
        R = r;
        G = g;
        B = b;
    }

    public static VtColor Default => default;

    public VtColorKind Kind { get; }

    /// <summary>The palette index, meaningful only when <see cref="Kind"/> is indexed.</summary>
    public byte Index => R;

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public static VtColor FromIndex(byte index) => new(VtColorKind.Indexed, index, 0, 0);

    public static VtColor FromRgb(byte r, byte g, byte b) => new(VtColorKind.Rgb, r, g, b);

    public bool Equals(VtColor other) =>
        Kind == other.Kind && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is VtColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, R, G, B);

    public static bool operator ==(VtColor left, VtColor right) => left.Equals(right);

    public static bool operator !=(VtColor left, VtColor right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        VtColorKind.Indexed => $"idx{Index}",
        VtColorKind.Rgb => $"#{R:X2}{G:X2}{B:X2}",
        _ => "default",
    };
}
