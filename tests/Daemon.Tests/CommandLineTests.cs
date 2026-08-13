using OneRemoteCli.Daemon.Cli;

namespace OneRemoteCli.Daemon.Tests;

public class CommandLineTests
{
    [Fact]
    public void TreatsTheFirstBareTokenAsTheProgram()
    {
        ParsedCommand parsed = CommandLine.Parse(["pwsh"]);

        Assert.Equal(CommandKind.Wrap, parsed.Kind);
        Assert.Equal("pwsh", parsed.Program);
        Assert.Empty(parsed.Args);
    }

    [Fact]
    public void PassesEverythingAfterTheProgramToTheChild()
    {
        ParsedCommand parsed = CommandLine.Parse(["pwsh", "-NoLogo", "-File", ".\\build.ps1"]);

        Assert.Equal("pwsh", parsed.Program);
        Assert.Equal(["-NoLogo", "-File", ".\\build.ps1"], parsed.Args);
    }

    /// <summary>
    /// The wrapper has to be safe to prepend to a command line the user already
    /// trusts, so it must not claim a flag that was meant for the child.
    /// </summary>
    [Fact]
    public void DoesNotStealItsOwnFlagsFromTheChild()
    {
        ParsedCommand parsed = CommandLine.Parse(["pwsh", "--name", "mine", "--no-agent"]);

        Assert.Equal("pwsh", parsed.Program);
        Assert.Equal(["--name", "mine", "--no-agent"], parsed.Args);
        Assert.Null(parsed.DisplayName);
        Assert.True(parsed.RequireAgent);
    }

    [Fact]
    public void ReadsOptionsThatComeBeforeTheProgram()
    {
        ParsedCommand parsed = CommandLine.Parse(["--name", "nightly build", "pwsh", "-NoLogo"]);

        Assert.Equal("nightly build", parsed.DisplayName);
        Assert.Equal("pwsh", parsed.Program);
        Assert.Equal(["-NoLogo"], parsed.Args);
    }

    [Fact]
    public void EndsItsOwnOptionsAtADoubleDash()
    {
        ParsedCommand parsed = CommandLine.Parse(["--name", "x", "--", "--no-agent", "-h"]);

        Assert.Equal(CommandKind.Wrap, parsed.Kind);
        Assert.Equal("x", parsed.DisplayName);
        Assert.Equal("--no-agent", parsed.Program);
        Assert.Equal(["-h"], parsed.Args);
        Assert.True(parsed.RequireAgent);
    }

    [Theory]
    [InlineData("agent", CommandKind.Agent)]
    [InlineData("login", CommandKind.Login)]
    [InlineData("logout", CommandKind.Logout)]
    [InlineData("status", CommandKind.Status)]
    [InlineData("install", CommandKind.Install)]
    [InlineData("uninstall", CommandKind.Uninstall)]
    public void RecognisesSubcommands(string token, CommandKind expected)
    {
        Assert.Equal(expected, CommandLine.Parse([token]).Kind);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    public void EverySubcommandIsDocumented(string token)
    {
        // A subcommand missing from the usage text exists only for whoever read the
        // source, which for an install command is nobody who needs it.
        Assert.Contains($"1remote {token}", CommandLine.Usage, StringComparison.Ordinal);
    }

    /// <summary>A program called <c>agent</c> is still a program when it has arguments.</summary>
    [Fact]
    public void OnlyTreatsSubcommandsAsSubcommandsWhenTheyStandAlone()
    {
        ParsedCommand parsed = CommandLine.Parse(["agent", "--verbose"]);

        Assert.Equal(CommandKind.Wrap, parsed.Kind);
        Assert.Equal("agent", parsed.Program);
    }

    [Fact]
    public void RequiresTheAgentUnlessTheUserOptsOut()
    {
        Assert.True(CommandLine.Parse(["pwsh"]).RequireAgent);
        Assert.False(CommandLine.Parse(["--no-agent", "pwsh"]).RequireAgent);
    }

    [Fact]
    public void ReportsUsageWhenGivenNothing()
    {
        ParsedCommand parsed = CommandLine.Parse([]);

        Assert.Equal(CommandKind.Help, parsed.Kind);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void ReportsUsageOnRequest(string flag)
    {
        Assert.Equal(CommandKind.Help, CommandLine.Parse([flag]).Kind);
    }

    [Fact]
    public void RejectsAnUnknownOption()
    {
        ParsedCommand parsed = CommandLine.Parse(["--nope", "pwsh"]);

        Assert.Equal(CommandKind.Help, parsed.Kind);
        Assert.Contains("--nope", parsed.Error);
    }

    [Fact]
    public void RejectsANameWithNoValue()
    {
        Assert.Contains("--name", CommandLine.Parse(["--name"]).Error);
    }

    [Fact]
    public void RejectsOptionsWithNoProgram()
    {
        Assert.Contains("No program", CommandLine.Parse(["--name", "x"]).Error);
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("C:\\Program Files\\x", "\"C:\\Program Files\\x\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void QuotesArgumentsTheWayTheRuntimeParsesThem(string argument, string expected)
    {
        Assert.Equal(expected, CommandLine.Quote(argument));
    }

    /// <summary>
    /// A trailing backslash is the classic case: unescaped, it would escape the
    /// closing quote and swallow the next argument. Windows paths hit this constantly.
    /// </summary>
    [Fact]
    public void DoublesBackslashesThatButtAgainstTheClosingQuote()
    {
        Assert.Equal("\"C:\\dir with space\\\\\"", CommandLine.Quote("C:\\dir with space\\"));
    }

    [Fact]
    public void LeavesBackslashesAloneWhenNoQuoteFollows()
    {
        Assert.Equal("C:\\dir\\file", CommandLine.Quote("C:\\dir\\file"));
    }

    [Fact]
    public void EncodesAProgramAndItsArgumentsAsOneCommandLine()
    {
        Assert.Equal(
            "pwsh -NoLogo -File \"C:\\my scripts\\build.ps1\"",
            CommandLine.Encode("pwsh", ["-NoLogo", "-File", "C:\\my scripts\\build.ps1"]));
    }

    /// <summary>
    /// Round-trips through the real parser the child will use, which is the only
    /// check that actually proves the quoting rules were applied correctly.
    /// Arguments are given pipe-separated because attributes cannot hold arrays.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with space|another one")]
    [InlineData("C:\\path with space\\|-x")]
    [InlineData("quote\"inside|back\\\\slash")]
    [InlineData("|-x")]
    public void QuotingSurvivesARoundTripThroughTheRuntimeParser(string pipeSeparatedArgs)
    {
        string[] args = pipeSeparatedArgs.Split('|');
        string encoded = CommandLine.Encode("prog.exe", args);

        Assert.Equal(args, SplitLikeTheRuntime(encoded).Skip(1).ToArray());
    }

    /// <summary>
    /// Implements the documented <c>CommandLineToArgvW</c> rules, so the test does not
    /// simply mirror the implementation it is checking.
    /// </summary>
    private static List<string> SplitLikeTheRuntime(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool has = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (c == '\\')
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        current.Append('"');
                    }

                    has = true;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                    has = true;
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (has)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    has = false;
                }

                continue;
            }

            current.Append(c);
            has = true;
        }

        if (has)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
