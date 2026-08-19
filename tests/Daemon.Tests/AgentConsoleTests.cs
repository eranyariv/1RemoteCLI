using OneRemoteCli.Daemon.Cli;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Deciding whether to hand the agent off to a process with no console.
/// <para>
/// The handoff itself needs a real console and a real process, so what is tested here
/// is the judgement that guards it, and the rule that a failed handoff still leaves an
/// agent running. Getting this wrong in the permissive direction takes away the
/// terminal a user is sitting in front of; getting the failure case wrong leaves the
/// machine with no agent at all, which is worse than the window this exists to remove.
/// </para>
/// </summary>
public sealed class AgentConsoleTests
{
    [Fact]
    public void AConsoleWithOnlyThisProcessOnItIsHandedOff()
    {
        Assert.Equal(AgentConsole.Verdict.HandOff, AgentConsole.Decide(1));
    }

    /// <summary>
    /// The console belongs to the shell, and someone is reading it. This is the answer
    /// that matters most: a user who typed <c>1remote agent</c> to watch it must keep
    /// both the output and the ability to stop it.
    /// </summary>
    [Fact]
    public void AConsoleSharedWithAShellIsLeftAlone()
    {
        Assert.Equal(AgentConsole.Verdict.StayHere, AgentConsole.Decide(2));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    public void SoIsOneSharedWithSeveral(int attached)
    {
        Assert.Equal(AgentConsole.Verdict.StayHere, AgentConsole.Decide(attached));
    }

    /// <summary>
    /// Zero means no console at all, which is the copy we started — it must run the
    /// agent rather than start another copy, or the handoff would never end. It is
    /// also what the API returns when it fails, and both want this same answer.
    /// </summary>
    [Fact]
    public void NoConsoleMeansThisIsAlreadyTheDetachedCopy()
    {
        Assert.Equal(AgentConsole.Verdict.StayHere, AgentConsole.Decide(0));
    }

    [Fact]
    public void NothingIsStartedWhenTheConsoleIsShared()
    {
        bool started = false;

        bool handedOff = AgentConsole.HandOffIfOurs(
            ["agent"],
            attachedProcesses: () => 2,
            start: _ =>
            {
                started = true;

                return true;
            });

        Assert.False(handedOff);
        Assert.False(started);
    }

    /// <summary>
    /// A handoff that cannot be started is not a reason to exit. An agent under a
    /// stray window is a blemish; no agent is a machine that cannot be reached.
    /// </summary>
    [Fact]
    public void AFailedHandoffRunsTheAgentHereInstead()
    {
        bool handedOff = AgentConsole.HandOffIfOurs(
            ["agent"],
            attachedProcesses: () => 1,
            start: _ => false);

        Assert.False(handedOff);
    }

    [Fact]
    public void ACompletedHandoffTellsTheCallerToLeave()
    {
        bool handedOff = AgentConsole.HandOffIfOurs(
            ["agent"],
            attachedProcesses: () => 1,
            start: _ => true);

        Assert.True(handedOff);
    }

    /// <summary>
    /// Whatever this process was told to do, the copy is told the same. Dropping an
    /// argument here would produce an agent that silently ignored it.
    /// </summary>
    [Fact]
    public void TheCopyIsGivenTheSameArguments()
    {
        string[]? passed = null;

        AgentConsole.HandOffIfOurs(
            ["agent", "--verbose"],
            attachedProcesses: () => 1,
            start: args =>
            {
                passed = args;

                return true;
            });

        Assert.Equal(["agent", "--verbose"], passed);
    }

    /// <summary>
    /// The executable is always quoted: it is installed under a path containing
    /// "Program Files" often enough, and an unquoted space there would start something
    /// else entirely, or nothing.
    /// </summary>
    [Fact]
    public void ThePathIsQuotedSoASpaceInItSurvives()
    {
        string line = AgentConsole.CommandLineFor(@"C:\Program Files\1remote.exe", ["agent"]);

        Assert.Equal(@"""C:\Program Files\1remote.exe"" agent", line);
    }

    [Fact]
    public void ArgumentsFollowTheExecutable()
    {
        string line = AgentConsole.CommandLineFor(@"C:\1remote.exe", ["agent", "--verbose"]);

        Assert.Equal(@"""C:\1remote.exe"" agent --verbose", line);
    }

    [Fact]
    public void AnArgumentWithASpaceIsQuotedToo()
    {
        string line = AgentConsole.CommandLineFor(@"C:\1remote.exe", ["agent", "two words"]);

        Assert.Equal(@"""C:\1remote.exe"" agent ""two words""", line);
    }
}
