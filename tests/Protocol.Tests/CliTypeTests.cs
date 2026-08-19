using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Protocol.Tests;

/// <summary>
/// What the agent guesses a session is running.
/// <para>
/// The rule is cheap to get subtly wrong and expensive to notice: a bad guess does
/// not fail, it just quietly offers Claude Code's commands to a shell. So the cases
/// pinned here are the shapes a program name actually arrives in — bare, with a
/// Windows extension, as a full path, and from an npm shim — rather than a single
/// tidy example of each type.
/// </para>
/// </summary>
public class CliTypeTests
{
    [Theory]
    [InlineData("claude", CliType.ClaudeCode)]
    [InlineData("CLAUDE", CliType.ClaudeCode)]
    [InlineData("claude.cmd", CliType.ClaudeCode)]
    [InlineData(@"C:\Users\eran\AppData\Roaming\npm\claude.ps1", CliType.ClaudeCode)]
    [InlineData("copilot", CliType.CopilotCli)]
    [InlineData("copilot.exe", CliType.CopilotCli)]
    [InlineData("pwsh", CliType.PowerShell)]
    [InlineData("powershell.exe", CliType.PowerShell)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", CliType.PowerShell)]
    [InlineData("cmd", CliType.Cmd)]
    [InlineData(@"C:\Windows\system32\cmd.exe", CliType.Cmd)]
    public void RecognisesAProgramHoweverItWasSpelled(string program, CliType expected)
    {
        Assert.Equal(expected, CliTypes.Detect(program));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("node")]
    [InlineData("git")]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    public void AdmitsWhenItDoesNotKnow(string program)
    {
        Assert.Equal(CliType.Generic, CliTypes.Detect(program));
    }

    [Fact]
    public void ReadsTheSubcommandThatChangesWhichProgramYouAreTalkingTo()
    {
        Assert.Equal(CliType.CopilotCli, CliTypes.Detect("gh", ["copilot"]));

        // Options in front of it are still the same invocation.
        Assert.Equal(CliType.CopilotCli, CliTypes.Detect("gh", ["--repo", "copilot"]));
    }

    [Fact]
    public void DoesNotClaimEveryGhInvocationIsCopilot()
    {
        Assert.Equal(CliType.Generic, CliTypes.Detect("gh", ["pr", "list"]));
        Assert.Equal(CliType.Generic, CliTypes.Detect("gh"));
    }

    /// <summary>
    /// A quoted path is what a Start Menu shortcut hands over, and the quote travels
    /// with it. Left in place it makes the name unparseable, and the session would
    /// show as Generic for no reason the user could ever discover.
    /// </summary>
    [Fact]
    public void SeesThroughTheQuotesAShortcutLeavesBehind()
    {
        Assert.Equal(
            CliType.PowerShell,
            CliTypes.Detect("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\""));
    }

    /// <summary>
    /// The shape every wrapped desktop shortcut has. A shortcut cannot start a `.cmd`
    /// shim on its own and stay open, so there is a shell in front of the tool, and
    /// the arguments arrive as the single string the original shortcut stored.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/k", "copilot --allow-all-tools --allow-all-paths --remote", CliType.CopilotCli)]
    [InlineData("cmd.exe", "/C", "claude", CliType.ClaudeCode)]
    [InlineData("powershell.exe", "-Command", "copilot", CliType.CopilotCli)]
    [InlineData("pwsh", "-c", "claude --dangerously-skip-permissions", CliType.ClaudeCode)]
    [InlineData("pwsh", "-File", @"C:\tools\claude.ps1", CliType.ClaudeCode)]
    [InlineData("powershell", "-Command", "& 'C:\\Users\\eran\\AppData\\Roaming\\npm\\copilot.cmd'", CliType.CopilotCli)]
    public void LooksThroughTheShellAShortcutPutsInFront(
        string program,
        string flag,
        string command,
        CliType expected)
    {
        Assert.Equal(expected, CliTypes.Detect(program, [flag, command]));
    }

    /// <summary>
    /// The same command line, already split into arguments, which is what a hand-typed
    /// <c>1remote -- cmd /k copilot --resume</c> produces.
    /// </summary>
    [Fact]
    public void DoesNotCareHowTheArgumentsWereSplit()
    {
        Assert.Equal(CliType.CopilotCli, CliTypes.Detect("cmd", ["/k", "copilot", "--resume"]));
        Assert.Equal(CliType.CopilotCli, CliTypes.Detect("powershell", ["-NoExit", "-Command", "copilot"]));
        Assert.Equal(CliType.CopilotCli, CliTypes.Detect("cmd", ["/k", "gh copilot"]));
    }

    /// <summary>
    /// One shell can start another, and the tool is still what is on screen.
    /// </summary>
    [Fact]
    public void FollowsAShellThroughAnotherShell()
    {
        Assert.Equal(CliType.ClaudeCode, CliTypes.Detect("cmd", ["/k", "pwsh -c claude"]));
    }

    /// <summary>
    /// The fallback matters as much as the match: a shell that was not asked to run
    /// anything, or was asked to run something unrecognised, is a shell. Answering
    /// Generic there would take away the PowerShell commands from a PowerShell prompt.
    /// </summary>
    [Fact]
    public void StaysAShellWhenItIsOnlyAShell()
    {
        Assert.Equal(CliType.Cmd, CliTypes.Detect("cmd"));
        Assert.Equal(CliType.Cmd, CliTypes.Detect("cmd", ["/k"]));
        Assert.Equal(CliType.Cmd, CliTypes.Detect("cmd", ["/k", "npm run dev"]));
        Assert.Equal(CliType.PowerShell, CliTypes.Detect("pwsh", ["-NoLogo", "-NoExit"]));
        Assert.Equal(CliType.PowerShell, CliTypes.Detect("pwsh", ["-Command", "Get-ChildItem"]));
    }

    /// <summary>
    /// A switch that only looks like one. <c>-EncodedCommand</c> carries base64 that
    /// this deliberately does not decode, and reading its payload as a program name
    /// would be a guess about a guess.
    /// </summary>
    [Fact]
    public void IgnoresSwitchesThatAreNotTheOneThatSaysWhatToRun()
    {
        Assert.Equal(
            CliType.PowerShell,
            CliTypes.Detect("powershell", ["-EncodedCommand", "Y29waWxvdA=="]));

        Assert.Equal(
            CliType.PowerShell,
            CliTypes.Detect("powershell", ["-ExecutionPolicy", "Bypass"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasAnAnswerForNothingAtAll(string? program)
    {
        Assert.Equal(CliType.Generic, CliTypes.Detect(program));
    }

    /// <summary>
    /// Every member needs a label, including any added later: the badge falling back
    /// to "Generic" for a type the user explicitly chose would look like the choice
    /// had not been saved.
    /// </summary>
    [Fact]
    public void NamesEveryTypeItHas()
    {
        foreach (CliType type in Enum.GetValues<CliType>())
        {
            string label = CliTypes.Label(type);

            Assert.False(string.IsNullOrWhiteSpace(label));

            if (type != CliType.Generic)
            {
                Assert.NotEqual("Generic", label);
            }
        }
    }
}
