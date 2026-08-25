namespace OneRemoteCli.Daemon;

/// <summary>
/// The two rules every browser-supplied name has to pass before it becomes part of a
/// path on this machine: the leaf is sanitized, and the result is proved to still sit
/// under the root it was built from.
/// <para>
/// Shared by the terminal upload store and the chat attachment store because the
/// rules are about Windows and about untrusted input, not about what either feature
/// does with the file afterwards. Everything that differs between the two — where the
/// bytes end up, who may read them, when they are deleted — deliberately stays in the
/// stores themselves.
/// </para>
/// </summary>
internal static class SafeTempFiles
{
    /// <summary>Leaves nothing that could traverse, name a device, or reach a shell.</summary>
    public static string SanitizeFileName(string original)
    {
        string leaf = original.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] sanitized =
        [
            .. leaf.Select(character =>
                char.IsControl(character) ||
                character == ':' ||
                character is '%' or '!' ||
                invalid.Contains(character)
                    ? '_'
                    : character),
        ];

        string result = new string(sanitized).Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..")
        {
            result = "attachment";
        }

        if (result.Length > 180)
        {
            string extension = Path.GetExtension(result);
            int stemLength = Math.Max(1, 180 - extension.Length);
            result = result[..stemLength] + extension;
        }

        string stem = Path.GetFileNameWithoutExtension(result);
        if (WindowsDeviceNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    /// <summary>
    /// Combines and then proves the result is still inside <paramref name="root"/>.
    /// Throws rather than returning a fallback: a path that escaped is a bug or an
    /// attack, and neither should end with bytes being written somewhere else.
    /// </summary>
    public static string ContainedPath(string root, string child)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, child));

        if (!ContainedBy(fullRoot, fullPath))
        {
            throw new InvalidOperationException("A temporary attachment path escaped its root.");
        }

        return fullPath;
    }

    public static bool ContainedBy(string root, string path)
    {
        string prefix = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> WindowsDeviceNames =
        new(
            [
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            ],
            StringComparer.OrdinalIgnoreCase);
}
