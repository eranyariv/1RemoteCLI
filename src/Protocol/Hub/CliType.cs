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
    /// How many shells deep to look before taking the outermost one at its word.
    /// <para>
    /// Two covers <c>cmd /k "pwsh -c claude"</c>, which is as far as anybody's
    /// shortcut goes. A bound rather than plain recursion because the argument list
    /// comes from a file on disk that anyone can write.
    /// </para>
    /// </summary>
    private const int Shells = 2;

    /// <summary>
    /// The type of a session running <paramref name="program"/> with
    /// <paramref name="args"/>, or <see cref="CliType.Generic"/> when nothing matches.
    /// </summary>
    public static CliType Detect(string? program, IReadOnlyList<string>? args = null) =>
        Detect(program, args, Shells);

    /// <summary>
    /// Detects a shortcut command line without first normalising the arguments.
    /// Shell links store their argument tail as one already-quoted string, so splitting
    /// it here with the same tokenizer used for nested shell commands avoids callers
    /// inventing subtly different quoting rules.
    /// </summary>
    public static CliType Detect(string? program, string? arguments) =>
        Detect(program, Tokenise(arguments ?? string.Empty).ToArray(), Shells);

    /// <summary>A stable command-line token for persisting an explicit user choice.</summary>
    public static string Token(CliType type) => type switch
    {
        CliType.Cmd => "cmd",
        CliType.PowerShell => "powershell",
        CliType.ClaudeCode => "claude-code",
        CliType.CopilotCli => "copilot",
        _ => "generic",
    };

    /// <summary>Reads a token accepted by <c>1remote --type</c>.</summary>
    public static bool TryParse(string? value, out CliType type)
    {
        type = (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "generic" => CliType.Generic,
            "cmd" or "command-prompt" => CliType.Cmd,
            "powershell" or "pwsh" => CliType.PowerShell,
            "claude" or "claude-code" => CliType.ClaudeCode,
            "copilot" or "copilot-cli" => CliType.CopilotCli,
            _ => (CliType)(-1),
        };

        return Enum.IsDefined(type);
    }

    private static CliType Detect(string? program, IReadOnlyList<string>? args, int depth)
    {
        string name = FileName(program);

        if (name.Length == 0)
        {
            return CliType.Generic;
        }

        if (ByName.TryGetValue(name, out CliType known))
        {
            // A shell is usually a doorway rather than a destination. Desktop shortcuts
            // for these tools are written as `cmd /k "copilot ..."` because a shortcut
            // that starts a `.cmd` shim directly closes the window the moment the tool
            // exits, so the shell is there to keep it open. Answering "cmd" is true
            // about what was launched and useless about what is running, which is the
            // question the badge exists to answer.
            if (known is CliType.Cmd or CliType.PowerShell && depth > 0 &&
                Hosted(known, args) is { Length: > 0 } hosted)
            {
                CliType inner = Detect(hosted[0], hosted[1..], depth - 1);

                if (inner != CliType.Generic)
                {
                    return inner;
                }
            }

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

    /// <summary>
    /// The command a shell was told to run, as a program followed by its arguments,
    /// or null when it was not told to run one.
    /// <para>
    /// Everything after the switch is the command, however the argument list happens
    /// to be split. A wrapped shortcut hands over one string — <c>/k</c> then
    /// <c>copilot --allow-all-tools</c> entire — because it copies the original's
    /// command line rather than re-quoting it, while a hand-typed invocation arrives
    /// already split. Joining and re-splitting treats both the same.
    /// </para>
    /// </summary>
    private static string[] Hosted(CliType shell, IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return [];
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (!IsRunSwitch(shell, args[i]))
            {
                continue;
            }

            List<string> command = [];

            for (int j = i + 1; j < args.Count; j++)
            {
                command.AddRange(Tokenise(args[j]));
            }

            // PowerShell's call operator, a leading dot-source, or a stray separator
            // sit where the program does without being one.
            while (command.Count > 0 && Noise.Contains(command[0]))
            {
                command.RemoveAt(0);
            }

            return [.. command];
        }

        return [];
    }

    /// <summary>Tokens that can precede a program without being one.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "&", ".", ";", "(", "{", "&&",
    };

    /// <summary>
    /// Whether an argument is the one that says "and here is what to run".
    /// <para>
    /// PowerShell's parameters can be abbreviated to any unambiguous prefix, and
    /// people do: <c>-c</c>, <c>-Command</c> and <c>-comm</c> are the same switch and
    /// all three turn up in shortcuts.
    /// </para>
    /// </summary>
    private static bool IsRunSwitch(CliType shell, string? arg)
    {
        string flag = (arg ?? string.Empty).Trim().Trim('"');

        if (flag.Length < 2 || (flag[0] != '-' && flag[0] != '/'))
        {
            return false;
        }

        string name = flag[1..];

        return shell == CliType.Cmd
            ? name.Equals("c", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("k", StringComparison.OrdinalIgnoreCase)
            : IsPrefixOf(name, "command") || IsPrefixOf(name, "file");
    }

    private static bool IsPrefixOf(string candidate, string whole) =>
        candidate.Length <= whole.Length &&
        whole.AsSpan(0, candidate.Length).Equals(candidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a command line on whitespace, keeping quoted runs together and dropping
    /// the quotes. Deliberately not a faithful implementation of anybody's quoting
    /// rules: this reads a command line to recognise a name in it, and never to run it.
    /// </summary>
    private static IEnumerable<string> Tokenise(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var token = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
