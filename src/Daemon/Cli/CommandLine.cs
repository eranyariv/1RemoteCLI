namespace OneRemoteCli.Daemon.Cli;

/// <summary>What the user asked <c>1remote</c> to do.</summary>
public enum CommandKind
{
    /// <summary>Run a program under a shareable pseudoconsole.</summary>
    Wrap,

    /// <summary>Start the per-machine tray agent.</summary>
    Agent,

    /// <summary>Sign in and cache a token.</summary>
    Login,

    /// <summary>Forget the current account and sign in as a different one.</summary>
    SwitchAccount,

    /// <summary>Forget the cached token.</summary>
    Logout,

    /// <summary>Report who is signed in and whether the token still works.</summary>
    Status,

    /// <summary>Start the agent now and at every logon, and add Start menu shortcuts.</summary>
    Install,

    /// <summary>Undo <see cref="Install"/>.</summary>
    Uninstall,

    /// <summary>
    /// Prove this build of the executable actually works. Meant for the publish script
    /// rather than for users, which is why it is absent from the help.
    /// </summary>
    SelfCheck,

    /// <summary>Print usage.</summary>
    Help,

    /// <summary>Print the version.</summary>
    Version,

    /// <summary>
    /// Copy an existing <c>.lnk</c> into one that starts the same program inside a
    /// shareable session.
    /// </summary>
    WrapShortcut,
}

/// <summary>A parsed command line.</summary>
/// <param name="Kind">Which subcommand was requested.</param>
/// <param name="Program">Program to launch, for <see cref="CommandKind.Wrap"/>.</param>
/// <param name="Args">Arguments to pass to that program, verbatim.</param>
/// <param name="DisplayName">Friendly session label from <c>--name</c>, if given.</param>
/// <param name="RequireAgent">
/// False only when the user passed <c>--no-agent</c>. See <see cref="CommandLine.NoAgentFlag"/>.
/// </param>
/// <param name="Error">Why parsing failed, or null when it succeeded.</param>
/// <param name="ShortcutPath">The <c>.lnk</c> to wrap, for <see cref="CommandKind.WrapShortcut"/>.</param>
/// <param name="OutputPath">Where to write the wrapped shortcut, when the user named a place.</param>
public sealed record ParsedCommand(
    CommandKind Kind,
    string? Program = null,
    IReadOnlyList<string>? Args = null,
    string? DisplayName = null,
    bool RequireAgent = true,
    string? Error = null,
    string? ShortcutPath = null,
    string? OutputPath = null)
{
    public IReadOnlyList<string> Args { get; init; } = Args ?? [];
}

/// <summary>
/// Parses <c>1remote</c>'s command line.
/// <para>
/// The governing rule is that only tokens *before* the program name are ours.
/// Everything from the program name onwards belongs to the child, untouched, so
/// that <c>1remote pwsh -NoLogo --name x</c> passes <c>--name x</c> to PowerShell
/// rather than quietly stealing it. That is what lets the wrapper be transparent:
/// a user can put <c>1remote</c> in front of any command line they already trust.
/// </para>
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Development escape hatch: run without an agent, and say so loudly.
    /// <para>
    /// The wrapper otherwise refuses to start when the agent is missing, because a
    /// session that looks shareable but is not is worse than no session at all. This
    /// flag exists so the wrapper can be exercised before an agent exists; it prints
    /// a banner every time, so it can never be mistaken for the real thing.
    /// </para>
    /// </summary>
    public const string NoAgentFlag = "--no-agent";

    /// <summary>
    /// Words that mean us rather than a program to run. Kept short on purpose: every
    /// entry is a name the user can no longer wrap without <c>--</c> or a path.
    /// </summary>
    private static readonly Dictionary<string, CommandKind> Subcommands = new(StringComparer.Ordinal)
    {
        ["agent"] = CommandKind.Agent,
        ["login"] = CommandKind.Login,
        ["switch-account"] = CommandKind.SwitchAccount,
        ["logout"] = CommandKind.Logout,
        ["status"] = CommandKind.Status,
        ["install"] = CommandKind.Install,
        ["uninstall"] = CommandKind.Uninstall,
        ["self-check"] = CommandKind.SelfCheck,
    };

    /// <summary>
    /// Not in <see cref="Subcommands"/> because that table is only consulted when the
    /// word is the last token, and this one is always followed by a path.
    /// </summary>
    private const string WrapShortcutCommand = "wrap-shortcut";

    public static ParsedCommand Parse(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0)
        {
            return new ParsedCommand(CommandKind.Help);
        }

        string? displayName = null;
        bool requireAgent = true;
        int i = 0;

        while (i < argv.Count)
        {
            string token = argv[i];

            switch (token)
            {
                case "-h" or "--help":
                    return new ParsedCommand(CommandKind.Help);

                case "--version":
                    return new ParsedCommand(CommandKind.Version);

                case "--name":
                    if (i + 1 >= argv.Count)
                    {
                        return Fail("--name requires a value.");
                    }

                    displayName = argv[i + 1];
                    i += 2;
                    continue;

                case NoAgentFlag:
                    requireAgent = false;
                    i++;
                    continue;

                // Explicit end of our options. Everything after belongs to the child,
                // even if it happens to look like one of our flags.
                case "--":
                    i++;
                    goto done;
            }

            if (token.StartsWith('-'))
            {
                return Fail($"Unknown option '{token}'.");
            }

            break;
        }

    done:
        if (i >= argv.Count)
        {
            return Fail("No program given.");
        }

        string program = argv[i];

        // Subcommands only count in the first non-option position, so `1remote agent`
        // starts the agent while `1remote pwsh agent` runs PowerShell.
        if (i == argv.Count - 1 && Subcommands.TryGetValue(program, out CommandKind subcommand))
        {
            return new ParsedCommand(subcommand);
        }

        // The one subcommand with arguments of its own, so the rule above cannot
        // recognise it — that rule requires the subcommand to be the last token. The
        // position rule still holds: `1remote pwsh wrap-shortcut` runs PowerShell.
        if (string.Equals(program, WrapShortcutCommand, StringComparison.Ordinal))
        {
            return ParseWrapShortcut(argv, i + 1);
        }

        return new ParsedCommand(
            CommandKind.Wrap,
            program,
            argv.Skip(i + 1).ToArray(),
            displayName,
            requireAgent);
    }

    /// <summary>
    /// <c>wrap-shortcut &lt;path.lnk&gt; [--output &lt;path.lnk&gt;]</c>.
    /// <para>
    /// Its own parser rather than more cases in the loop above, because the loop's
    /// governing rule is that a bare word ends our options and starts the child's
    /// command line — and here a bare word is the shortcut, with our options still to
    /// come after it.
    /// </para>
    /// </summary>
    private static ParsedCommand ParseWrapShortcut(IReadOnlyList<string> argv, int i)
    {
        string? source = null;
        string? output = null;

        while (i < argv.Count)
        {
            string token = argv[i];

            if (token == "--output")
            {
                if (i + 1 >= argv.Count)
                {
                    return Fail("--output requires a value.");
                }

                output = argv[i + 1];
                i += 2;
                continue;
            }

            if (token.StartsWith('-'))
            {
                return Fail($"Unknown option '{token}'.");
            }

            if (source is not null)
            {
                // Two paths almost always means an unquoted one with a space in it, and
                // wrapping the wrong file silently is worse than saying so.
                return Fail("wrap-shortcut takes one shortcut. Quote the path if it contains spaces.");
            }

            source = token;
            i++;
        }

        return source is null
            ? Fail("wrap-shortcut needs the path of a .lnk to wrap.")
            : new ParsedCommand(CommandKind.WrapShortcut, ShortcutPath: source, OutputPath: output);
    }

    private static ParsedCommand Fail(string message) => new(CommandKind.Help, Error: message);

    /// <summary>
    /// Joins a program and its arguments into the single string <c>CreateProcess</c> wants,
    /// quoted the way the C runtime will parse it back apart.
    /// </summary>
    public static string Encode(string program, IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(args);

        var builder = new System.Text.StringBuilder();
        builder.Append(Quote(program));

        foreach (string arg in args)
        {
            builder.Append(' ').Append(Quote(arg));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Applies the Windows command-line quoting rules to one argument.
    /// <para>
    /// Backslashes are only special immediately before a quote, so they are doubled
    /// there and left alone everywhere else. Getting this wrong mangles Windows paths,
    /// which are exactly the arguments users pass most often.
    /// </para>
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !argument.AsSpan().ContainsAny(" \t\n\v\""))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder(argument.Length + 2);
        builder.Append('"');

        for (int i = 0; i < argument.Length; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Trailing run: doubled, because the closing quote follows it.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Usage text, printed for <c>--help</c> and for any parse error.</summary>
    public const string Usage = """
        1remote - attach a phone to a terminal session on this machine.

        Usage:
          1remote [options] <program> [args...]   Run a program in a shareable session
          1remote agent                           Start the per-machine agent
          1remote login                           Sign in
          1remote switch-account                  Sign in as a different account
          1remote logout                          Forget the cached sign-in
          1remote status                          Show who is signed in
          1remote install                         Start the agent now and at every logon
          1remote uninstall                       Stop starting the agent at logon
          1remote wrap-shortcut <shortcut.lnk>    Copy a shortcut into a shareable one

        Options:
          --name <text>   Friendly name for the session (defaults to the program name)
          --no-agent      Run without the agent. The session is NOT shareable.
          --output <path> Where wrap-shortcut writes (defaults to beside the original)
          --version       Print the version
          -h, --help      Print this help

        Everything after the program name is passed to it unchanged. Use -- to end
        1remote's own options explicitly:

          1remote --name "nightly build" -- pwsh -NoLogo -File .\build.ps1
        """;
}
