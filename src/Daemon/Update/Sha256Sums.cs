namespace OneRemoteCli.Daemon.Update;

/// <summary>
/// Reading <c>SHA256SUMS.txt</c>, which is the only thing standing between a download
/// and being run.
/// <para>
/// Parsed by hand rather than with a clever expression, for the reason
/// <c>scripts/install.ps1</c> gives at the same job: a parse that quietly matches
/// nothing and hands back an empty string turns the check into decoration, and a check
/// that is decoration is worse than no check because it is believed.
/// </para>
/// </summary>
public static class Sha256Sums
{
    /// <summary>Length of a SHA-256 written as hex, which is the only shape accepted.</summary>
    private const int HashLength = 64;

    /// <summary>
    /// The published hash for one asset, lowercased, or null if the file does not list
    /// it.
    /// <para>
    /// Also null for a file that is not a checksum list at all. GitHub answers a
    /// download URL it cannot resolve with an HTML page and a 200, which lands on disk
    /// looking like a file; refusing anything whose first field is not sixty-four hex
    /// characters is what stops that page being treated as an answer.
    /// </para>
    /// </summary>
    public static string? Find(string? contents, string asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);

        if (string.IsNullOrWhiteSpace(contents))
        {
            return null;
        }

        foreach (string line in contents.Split('\n'))
        {
            string[] fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length != 2)
            {
                continue;
            }

            // The name is written with a leading '*' by some tools, meaning "binary
            // mode". Ours does not, but accepting it costs one line and its absence
            // would present as "the release does not list this asset".
            string name = fields[1].TrimStart('*');

            if (!string.Equals(name, asset, StringComparison.OrdinalIgnoreCase) || !IsHash(fields[0]))
            {
                continue;
            }

            return fields[0].ToLowerInvariant();
        }

        return null;
    }

    private static bool IsHash(string value)
    {
        if (value.Length != HashLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
