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
