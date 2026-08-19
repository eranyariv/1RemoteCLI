using System.Globalization;

namespace OneRemoteCli.Daemon.Update;

/// <summary>
/// A product version as this repository writes them, and the one question anyone asks
/// of one: is that release newer than what is running here.
/// <para>
/// The display form is <c>x.yy</c> — see <c>Directory.Build.props</c> — and the minor
/// part is a number, not two characters. Comparing the strings would be wrong at the
/// only moment it matters: <c>"0.9"</c> sorts after <c>"0.10"</c>, so a machine on 0.9
/// would be told it was ahead of every release for the next ninety of them.
/// </para>
/// <para>
/// Anything that will not parse is not a version, and an unparseable release is never
/// offered. The alternative — treating "could not read it" as "probably newer" — would
/// have the agent download and install whatever a mangled tag pointed at.
/// </para>
/// </summary>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(IReadOnlyList<int> parts, string text)
    {
        Parts = parts;
        Text = text;
    }

    /// <summary>The numeric components, most significant first.</summary>
    private IReadOnlyList<int>? Parts { get; }

    /// <summary>How the version is written, without any leading <c>v</c>.</summary>
    public string Text { get; }

    /// <summary>
    /// Reads a version from a release tag (<c>v0.13</c>) or a bare version (<c>0.13</c>).
    /// <para>
    /// A third part is accepted although nothing publishes one yet, so that a future
    /// <c>0.13.1</c> is understood by agents shipped before it rather than ignored by
    /// them — an update mechanism that cannot be extended is one that has to be
    /// replaced by hand on every machine it is running on.
    /// </para>
    /// </summary>
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // A build-metadata suffix is not part of the ordering, and the SDK will attach
        // one to the informational version if anybody ever turns that back on.
        int plus = text.IndexOf('+', StringComparison.Ordinal);

        if (plus >= 0)
        {
            text = text[..plus];
        }

        string[] fields = text.Split('.');

        if (fields.Length is < 2 or > 3)
        {
            return false;
        }

        int[] parts = new int[fields.Length];

        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(parts, text);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a release worth offering to a machine
    /// running <paramref name="current"/>.
    /// <para>
    /// Strictly newer. Equal is the common case — most checks find the release the
    /// machine is already on — and older happens to anyone running a build from source
    /// ahead of the tag, who must not be quietly moved backwards.
    /// </para>
    /// </summary>
    public static bool IsUpgrade(string? candidate, string? current) =>
        TryParse(candidate, out ReleaseVersion to)
        && TryParse(current, out ReleaseVersion from)
        && to.CompareTo(from) > 0;

    public int CompareTo(ReleaseVersion other)
    {
        IReadOnlyList<int> mine = Parts ?? [];
        IReadOnlyList<int> theirs = other.Parts ?? [];

        // A missing part is zero, so 0.13 and 0.13.0 are the same release rather than
        // one being an upgrade over the other in whichever direction the lengths fell.
        for (int i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
        {
            int difference = At(mine, i) - At(theirs, i);

            if (difference != 0)
            {
                return difference < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    private static int At(IReadOnlyList<int> parts, int index) => index < parts.Count ? parts[index] : 0;

    public override string ToString() => Text ?? string.Empty;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
