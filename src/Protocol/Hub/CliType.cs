namespace OneRemoteCli.Protocol.Hub;

/// <summary>
/// Which command-line program a session is hosting, as far as the agent can tell.
/// <para>
/// This exists so the phone can offer the right buttons. A terminal on a phone is
/// mostly a screen you cannot type into comfortably, and the difference between a
/// usable session and a frustrating one is whether the things you actually need —
/// Claude Code's <c>/clear</c>, PowerShell's history — are one tap away. That
/// requires knowing what is running, which nothing in the system previously did.
/// </para>
/// <para>
/// It is a hint, never a control. Nothing about how a session is relayed, framed or
/// interrupted changes with the type: the PTY stays a PTY and the bytes stay bytes.
/// The worst a wrong answer can do is offer a menu of commands the program does not
/// have, which is why guessing is acceptable here and would not be anywhere else.
/// </para>
/// </summary>
public enum CliType
{
    /// <summary>Something we do not recognise, or a session that has not said.</summary>
    Generic = 0,

    /// <summary>The Windows command processor, <c>cmd.exe</c>.</summary>
    Cmd = 1,

    /// <summary>Windows PowerShell or PowerShell 7, which share a command vocabulary.</summary>
    PowerShell = 2,

    /// <summary>Anthropic's Claude Code CLI.</summary>
    ClaudeCode = 3,

    /// <summary>The GitHub Copilot CLI.</summary>
    CopilotCli = 4,
}

/// <summary>
/// Works out a <see cref="CliType"/> from what the wrapper was asked to run.
/// <para>
/// Deliberately a pure function of the command line rather than anything observed
/// from the session's output. Sniffing the stream would be more accurate for the
/// awkward cases and is the wrong trade twice over: it would put a parser on the
/// hottest path in the product, and it would mean the type arrives some indefinite
/// time after the session does, so the buttons would appear a second or two late on
/// the one screen where the user is already waiting.
/// </para>
/// </summary>
public static class CliTypes
{
    /// <summary>
    /// Names matched against the program's file name, without directory or extension.
    /// <para>
    /// Extension-less on purpose. The same tool reaches the wrapper as
    /// <c>claude</c> from a shell that resolved it, as <c>claude.cmd</c> from an npm
    /// shim, and as a full path to <c>claude.ps1</c> from a Start Menu shortcut, and
    /// all three are the same program to the person looking at the screen.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, CliType> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = CliType.Cmd,
        ["powershell"] = CliType.PowerShell,
        ["pwsh"] = CliType.PowerShell,
        ["claude"] = CliType.ClaudeCode,
        ["copilot"] = CliType.CopilotCli,
    };

    /// <summary>
    /// The type of a session running <paramref name="program"/> with
    /// <paramref name="args"/>, or <see cref="CliType.Generic"/> when nothing matches.
    /// </summary>
    public static CliType Detect(string? program, IReadOnlyList<string>? args = null)
    {
        string name = FileName(program);

        if (name.Length == 0)
        {
            return CliType.Generic;
        }

        if (ByName.TryGetValue(name, out CliType known))
        {
            return known;
        }

        // `gh copilot` was how the Copilot CLI shipped before it had a launcher of its
        // own, and a subcommand is the one argument worth reading: it changes which
        // program you are talking to, rather than how that program behaves.
        if (name.Equals("gh", StringComparison.OrdinalIgnoreCase) &&
            FirstOperand(args) is { } sub &&
            sub.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return CliType.CopilotCli;
        }

        return CliType.Generic;
    }

    /// <summary>What to put on a badge. Short, because it is sharing a row with a name.</summary>
    public static string Label(CliType type) => type switch
    {
        CliType.Cmd => "cmd",
        CliType.PowerShell => "PowerShell",
        CliType.ClaudeCode => "Claude Code",
        CliType.CopilotCli => "Copilot CLI",
        _ => "Generic",
    };

    /// <summary>
    /// The program's bare name.
    /// <para>
    /// Quotes are stripped first because a program that arrives from a shortcut has
    /// been through a command line, and <c>"C:\Program Files\..."</c> is a path with a
    /// quote in it as far as <see cref="Path"/> is concerned.
    /// </para>
    /// </summary>
    private static string FileName(string? program)
    {
        string trimmed = (program ?? string.Empty).Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            string name = Path.GetFileNameWithoutExtension(trimmed);

            // A trailing separator leaves nothing behind; fall back rather than claim
            // the program has no name at all.
            return name.Length > 0 ? name : trimmed;
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
    }

    /// <summary>The first argument that is not an option, which is where a subcommand is.</summary>
    private static string? FirstOperand(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (string arg in args)
        {
            if (!string.IsNullOrWhiteSpace(arg) && !arg.StartsWith('-'))
            {
                return arg;
            }
        }

        return null;
    }
}
