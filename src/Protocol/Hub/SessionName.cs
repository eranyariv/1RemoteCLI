using System.Globalization;
using System.Text;

namespace OneRemoteCli.Protocol.Hub;

/// <summary>
/// Cleans a name the user typed before anything else is allowed to see it.
/// <para>
/// This is the only field in the product whose contents are chosen by a person and
/// then rendered somewhere that person is not standing: a Web Push notification on a
/// phone, a terminal header, the machine list on another device. Every one of those
/// is a surface where a control character, a line break or a right-to-left override
/// does something other than print, so the value is normalised once, here, at the
/// point it enters the hub — rather than at each of the places it leaves.
/// </para>
/// <para>
/// Deliberately in the shared protocol assembly rather than in the hub. The rule is
/// part of what a name <i>is</i> on this wire, and a second implementation that drifts
/// would be worse than none.
/// </para>
/// </summary>
public static class SessionName
{
    /// <summary>
    /// The longest name that is kept, in text elements.
    /// <para>
    /// Chosen for the narrowest place it is read — a notification title on a phone,
    /// which has already run out of room well before this. A cap that only bites on
    /// absurd input is the point: it exists so a name cannot be used as a paragraph,
    /// not to ration reasonable ones.
    /// </para>
    /// </summary>
    public const int MaxLength = 60;

    /// <summary>
    /// Normalises a name, or returns null when nothing usable is left.
    /// <para>
    /// Null is the answer for blank input too, and that is the feature: it is how the
    /// user clears a custom name and gets the agent's own back.
    /// </para>
    /// </summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var clean = new StringBuilder(name.Length);
        bool pendingSpace = false;

        foreach (Rune rune in name.EnumerateRunes())
        {
            // Whitespace is tested before danger, not after, because the two overlap:
            // a newline and a tab are both control characters. Testing danger first
            // dropped them outright and ran the words either side of them together.
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = clean.Length > 0;
                continue;
            }

            if (IsDangerous(rune))
            {
                continue;
            }

            if (pendingSpace)
            {
                clean.Append(' ');
                pendingSpace = false;
            }

            clean.Append(rune);
        }

        return clean.Length == 0 ? null : Truncate(clean.ToString());
    }

    /// <summary>
    /// What to call a session, in the order a human would expect: what the user chose,
    /// then what the agent called it, then the program it is running.
    /// <para>
    /// One function, because every place that renders a session name has to agree —
    /// most of all the push notification, which is the whole reason the custom name
    /// lives at the hub instead of in a browser's local storage.
    /// </para>
    /// </summary>
    public static string Best(string? customName, string? displayName, string program)
    {
        if (!string.IsNullOrWhiteSpace(customName))
        {
            return customName;
        }

        return string.IsNullOrWhiteSpace(displayName) ? program : displayName;
    }

    /// <summary>
    /// Characters that do something other than print.
    /// <para>
    /// Control characters would be interpreted by the terminal header; the bidi
    /// formatting characters can reverse the visible order of everything after them,
    /// which is how a name is made to read as something it is not. Surrogates and
    /// unassigned values are dropped for the same reason nothing else lets them
    /// through: no legitimate name contains one.
    /// </para>
    /// </summary>
    private static bool IsDangerous(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;

    /// <summary>
    /// Cuts to <see cref="MaxLength"/> without splitting a character in half.
    /// <para>
    /// By text element rather than by char: an emoji is two chars and a flag is four,
    /// and cutting one down the middle produces a replacement glyph on the device that
    /// renders it.
    /// </para>
    /// </summary>
    private static string Truncate(string name)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(name);
        int elements = 0;

        while (enumerator.MoveNext())
        {
            elements++;

            if (elements > MaxLength)
            {
                return name[..enumerator.ElementIndex].TrimEnd();
            }
        }

        return name;
    }
}
