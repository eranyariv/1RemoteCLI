using System.Text;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// The DEC special graphics character set: how programs drew boxes before Unicode.
/// <para>
/// Still needed. Anything built on curses or driven by <c>tput</c> selects this set with
/// <c>ESC ( 0</c> and then writes <c>lqk</c> to mean the top of a box. Without the
/// mapping the phone shows literal letters where the desk terminal shows a border, which
/// is exactly the kind of divergence that makes a remote view untrustworthy.
/// </para>
/// </summary>
public static class DecSpecialGraphics
{
    /// <summary>Replacements for <c>0x5F</c> through <c>0x7E</c>, in order.</summary>
    private static readonly char[] Table =
    [
        '\u00A0', // _ no-break space
        '\u25C6', // ` diamond
        '\u2592', // a checkerboard
        '\u2409', // b HT symbol
        '\u240C', // c FF symbol
        '\u240D', // d CR symbol
        '\u240A', // e LF symbol
        '\u00B0', // f degree
        '\u00B1', // g plus/minus
        '\u2424', // h NL symbol
        '\u240B', // i VT symbol
        '\u2518', // j lower right corner
        '\u2510', // k upper right corner
        '\u250C', // l upper left corner
        '\u2514', // m lower left corner
        '\u253C', // n crossing lines
        '\u23BA', // o horizontal line, scan 1
        '\u23BB', // p horizontal line, scan 3
        '\u2500', // q horizontal line, scan 5
        '\u23BC', // r horizontal line, scan 7
        '\u23BD', // s horizontal line, scan 9
        '\u251C', // t left tee
        '\u2524', // u right tee
        '\u2534', // v bottom tee
        '\u252C', // w top tee
        '\u2502', // x vertical line
        '\u2264', // y less than or equal
        '\u2265', // z greater than or equal
        '\u03C0', // { pi
        '\u2260', // | not equal
        '\u00A3', // } sterling
        '\u00B7', // ~ centre dot
    ];

    /// <summary>
    /// The box-drawing character <paramref name="rune"/> stands for, or the rune itself
    /// when it is outside the mapped range. Digits, capitals and punctuation are
    /// unaffected, which is why a program can leave the set selected while writing them.
    /// </summary>
    public static Rune Map(Rune rune)
    {
        int value = rune.Value;

        return value is >= 0x5F and <= 0x7E ? new Rune(Table[value - 0x5F]) : rune;
    }
}
