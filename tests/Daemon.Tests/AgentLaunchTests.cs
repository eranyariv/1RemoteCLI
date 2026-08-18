using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Starting the agent at the end of an install (issue #100).
/// <para>
/// Only the choice between the three ways this can go is exercised. The ways
/// themselves — triggering a real scheduled task, launching a real process — would
/// leave an agent running on whichever machine happened to run the tests, competing
/// for the pipe with the one the developer is using.
/// </para>
/// </summary>
public sealed class AgentLaunchTests
{
    private static Func<StepResult> Never(Action onCall) => () =>
    {
        onCall();
        return StepResult.Success("should not happen");
    };

    /// <summary>
    /// The upgrade path, and the reason this is checked at all: install.ps1 replaces
    /// the executable of a machine that was already set up. Starting a second agent
    /// would produce a process that exits with "an agent is already running", which
    /// reads like the install broke something that was working.
    /// </summary>
    [Fact]
    public void AnAgentThatIsAlreadyRunningIsLeftAlone()
    {
        StepResult result = AgentLaunch.Start(
            isRunning: () => true,
            taskIsRegistered: () =>
            {
                Assert.Fail("Asked about the task with an agent already up.");
                return false;
            },
            runTask: Never(() => Assert.Fail("Started a second agent.")),
            launchDetached: Never(() => Assert.Fail("Started a second agent.")));

        Assert.True(result.Ok);
    }

    /// <summary>
    /// Through the task wherever there is one, so what runs now is exactly what will
    /// run at every logon: same account, same environment, same arguments. A machine
    /// where the install works and the logon does not is a machine nobody can debug.
    /// </summary>
    [Fact]
    public void TheRegisteredTaskIsPreferredToStartingTheProcessDirectly()
    {
        bool viaTask = false;

        AgentLaunch.Start(
            isRunning: () => false,
            taskIsRegistered: () => true,
            runTask: () =>
            {
                viaTask = true;
                return StepResult.Success("started");
            },
            launchDetached: Never(() => Assert.Fail("Bypassed the task that is registered.")));

        Assert.True(viaTask);
    }

    /// <summary>
    /// Policy refused the task, so the Run key is what will start the agent at logon —
    /// and there is nothing to trigger now. The direct launch is the only thing left.
    /// </summary>
    [Fact]
    public void WithNoTaskRegisteredTheProcessIsStartedDirectly()
    {
        bool launched = false;

        AgentLaunch.Start(
            isRunning: () => false,
            taskIsRegistered: () => false,
            runTask: Never(() => Assert.Fail("Triggered a task that is not registered.")),
            launchDetached: () =>
            {
                launched = true;
                return StepResult.Success("started");
            });

        Assert.True(launched);
    }

    /// <summary>
    /// Registered but refused on demand. Rare, and not a reason to leave the user with
    /// no agent when there is still a way to start one.
    /// </summary>
    [Fact]
    public void ATaskThatRefusesToRunFallsBackToStartingTheProcessDirectly()
    {
        bool launched = false;

        StepResult result = AgentLaunch.Start(
            isRunning: () => false,
            taskIsRegistered: () => true,
            runTask: () => StepResult.Failure("access is denied"),
            launchDetached: () =>
            {
                launched = true;
                return StepResult.Success("started");
            });

        Assert.True(launched);
        Assert.True(result.Ok);
    }

    [Fact]
    public void WhenNothingCanStartTheAgentTheInstallIsToldSo()
    {
        StepResult result = AgentLaunch.Start(
            isRunning: () => false,
            taskIsRegistered: () => true,
            runTask: () => StepResult.Failure("access is denied"),
            launchDetached: () => StepResult.Failure("windows said no"));

        Assert.False(result.Ok);
        Assert.Equal("windows said no", result.Message);
    }

    /// <summary>
    /// The signal is the agent's pipe rather than a process called <c>1remote</c>:
    /// every wrapped session is one of those too, so a name check would see
    /// <c>1remote claude</c> and decide the agent was up.
    /// </summary>
    [Fact]
    public void RunningIsAnsweredByAskingWindowsRatherThanByRemembering()
    {
        // No agent is expected under the test runner, and either answer is a real one
        // read off the machine — the point is that it does not throw on a box with no
        // pipe of that name.
        _ = AgentLaunch.IsRunning();
    }
}
