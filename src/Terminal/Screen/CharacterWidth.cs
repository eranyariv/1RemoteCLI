using System.Globalization;
using System.Text;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// How many columns a character occupies.
/// <para>
/// The terminal has already made this decision by the time the bytes reach us — it laid
/// the character out in its grid and its subsequent cursor movements assume that layout.
/// The emulator has to reach the same answer or every column after a CJK character or an
/// emoji is off by one, which shows up as a prompt that drifts sideways.
/// </para>
/// <para>
/// The table is the East Asian Width Wide and Fullwidth ranges plus the emoji blocks
/// that terminals render double-width. It is approximate at the edges — Unicode adds
/// characters faster than tables get updated, and terminals disagree with each other
/// about the newest ones — but it is exact for everything these CLIs emit: box drawing,
/// braille spinners, check marks and arrows are all narrow, and CJK and emoji are wide.
/// </para>
/// </summary>
public static class CharacterWidth
{
    /// <summary>Inclusive codepoint ranges rendered two columns wide.</summary>
    private static readonly (int Low, int High)[] WideRanges =
    [
        (0x1100, 0x115F),   // Hangul Jamo, initial consonants
        (0x231A, 0x231B),   // watch, hourglass
        (0x2329, 0x232A),   // angle brackets
        (0x23E9, 0x23EC),
        (0x23F0, 0x23F0),
        (0x23F3, 0x23F3),
        (0x25FD, 0x25FE),
        (0x2614, 0x2615),
        (0x2648, 0x2653),
        (0x267F, 0x267F),
        (0x2693, 0x2693),
        (0x26A1, 0x26A1),
        (0x26AA, 0x26AB),
        (0x26BD, 0x26BE),
        (0x26C4, 0x26C5),
        (0x26CE, 0x26CE),
        (0x26D4, 0x26D4),
        (0x26EA, 0x26EA),
        (0x26F2, 0x26F3),
        (0x26F5, 0x26F5),
        (0x26FA, 0x26FA),
        (0x26FD, 0x26FD),
        (0x2705, 0x2705),
        (0x270A, 0x270B),
        (0x2728, 0x2728),
        (0x274C, 0x274C),
        (0x274E, 0x274E),
        (0x2753, 0x2755),
        (0x2757, 0x2757),
        (0x2795, 0x2797),
        (0x27B0, 0x27B0),
        (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C),
        (0x2B50, 0x2B50),
        (0x2B55, 0x2B55),
        (0x2E80, 0x303E),   // CJK radicals through CJK symbols
        (0x3041, 0x33FF),   // kana, Hangul compatibility, CJK compatibility
        (0x3400, 0x4DBF),   // CJK extension A
        (0x4E00, 0x9FFF),   // CJK unified ideographs
        (0xA000, 0xA4CF),   // Yi
        (0xA960, 0xA97F),   // Hangul Jamo extended A
        (0xAC00, 0xD7A3),   // Hangul syllables
        (0xF900, 0xFAFF),   // CJK compatibility ideographs
        (0xFE10, 0xFE19),   // vertical forms
        (0xFE30, 0xFE6F),   // CJK compatibility forms
        (0xFF00, 0xFF60),   // fullwidth forms
        (0xFFE0, 0xFFE6),   // fullwidth signs
        (0x16FE0, 0x16FE4),
        (0x17000, 0x18CD5), // Tangut
        (0x1B000, 0x1B2FF), // kana supplement
        (0x1F004, 0x1F004),
        (0x1F0CF, 0x1F0CF),
        (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A),
        (0x1F200, 0x1F320),
        (0x1F32D, 0x1F335),
        (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393),
        (0x1F3A0, 0x1F3CA),
        (0x1F3CF, 0x1F3D3),
        (0x1F3E0, 0x1F3F0),
        (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E),
        (0x1F440, 0x1F440),
        (0x1F442, 0x1F4FC),
        (0x1F4FF, 0x1F53D),
        (0x1F54B, 0x1F54E),
        (0x1F550, 0x1F567),
        (0x1F57A, 0x1F57A),
        (0x1F595, 0x1F596),
        (0x1F5A4, 0x1F5A4),
        (0x1F5FB, 0x1F64F),
        (0x1F680, 0x1F6C5),
        (0x1F6CC, 0x1F6CC),
        (0x1F6D0, 0x1F6D2),
        (0x1F6EB, 0x1F6EC),
        (0x1F6F4, 0x1F6FC),
        (0x1F7E0, 0x1F7EB),
        (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945),
        (0x1F947, 0x1F9FF),
        (0x1FA70, 0x1FAFF),
        (0x20000, 0x2FFFD), // CJK extension B and beyond
        (0x30000, 0x3FFFD),
    ];

    /// <summary>Columns occupied: 0 for a combining mark, 2 for a wide character, otherwise 1.</summary>
    public static int Of(Rune rune)
    {
        int value = rune.Value;

        if (value == 0)
        {
            return 0;
        }

        if (IsZeroWidth(rune))
        {
            return 0;
        }

        // Everything below the first wide range is narrow, which is the overwhelmingly
        // common case and worth not binary-searching for.
        return value < 0x1100 ? 1 : IsWide(value) ? 2 : 1;
    }

    /// <summary>
    /// True for characters that attach to the preceding one rather than taking a column
    /// of their own.
    /// </summary>
    public static bool IsZeroWidth(Rune rune)
    {
        int value = rune.Value;

        // The zero-width space and joiners are Format characters that must not consume
        // a column; the soft hyphen is also Format but does occupy one.
        if (value is 0x200B or 0x200C or 0x200D or 0xFEFF)
        {
            return true;
        }

        if (value == 0xAD)
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format;
    }

    private static bool IsWide(int value)
    {
        int low = 0;
        int high = WideRanges.Length - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            (int rangeLow, int rangeHigh) = WideRanges[mid];

            if (value < rangeLow)
            {
                high = mid - 1;
            }
            else if (value > rangeHigh)
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
