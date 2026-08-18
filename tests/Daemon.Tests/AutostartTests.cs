using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The one rule about starting at logon: a scheduled task, or the Run key if that
/// could not be done.
/// <para>
/// It lives on its own because there are now two callers with nothing else in common
/// — <c>1remote install</c> and the settings window's checkbox — and a checkbox that
/// set autostart up differently from the installer would produce machines that behave
/// differently depending on which one the user happened to use.
/// </para>
/// </summary>
public sealed class AutostartTests
{
    private static StepResult Ok(string message) => StepResult.Success(message);

    private static StepResult Failed(string message) => StepResult.Failure(message);

    [Fact]
    public void PrefersTheScheduledTaskAndRemovesTheFallback()
    {
        // Both would otherwise start an agent each, and the second exits immediately
        // reporting one is already running — which reads to the user as a crash on
        // every logon.
        bool removedRunKey = false;

        IReadOnlyList<StepResult> steps = Autostart.Enable(
            () => Ok("task registered"),
            () =>
            {
                removedRunKey = true;
                return Ok("run key removed");
            },
            () => throw new InvalidOperationException("The Run key must not be written when the task worked."));

        Assert.True(removedRunKey);
        Assert.All(steps, step => Assert.True(step.Ok));
    }

    [Fact]
    public void FallsBackToTheRunKeyWhenTheTaskCannotBeRegistered()
    {
        // Locked-down machines and some managed estates refuse Task Scheduler outright,
        // and an agent that does not come back after a reboot is an agent nobody trusts.
        IReadOnlyList<StepResult> steps = Autostart.Enable(
            () => Failed("schtasks refused"),
            () => Ok("nothing to remove"),
            () => Ok("run key written"));

        Assert.True(steps[^1].Ok);
        Assert.Contains(steps, step => step.Message.Contains("run key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnablingAlwaysReportsTheSameNumberOfSteps()
    {
        // The installer prints these, and a list whose length depends on which path was
        // taken reads as though something was skipped.
        Assert.Equal(
            Autostart.Enable(() => Ok("a"), () => Ok("b"), () => Ok("c")).Count,
            Autostart.Enable(() => Failed("a"), () => Ok("b"), () => Ok("c")).Count);
    }

    [Fact]
    public void DisablingRemovesBoth()
    {
        // Not just the one that is in use: a machine that fell back to the Run key at
        // some point has both, and leaving either behind means the agent still starts.
        bool task = false;
        bool runKey = false;

        Autostart.Disable(
            () =>
            {
                task = true;
                return Ok("task removed");
            },
            () =>
            {
                runKey = true;
                return Ok("run key removed");
            });

        Assert.True(task);
        Assert.True(runKey);
    }

    [Fact]
    public void ASummaryOfFailureSaysWhatDidNotHappen()
    {
        string summary = Autostart.Summarise([Ok("a"), Failed("b")], enabling: true);

        Assert.DoesNotContain("will start", summary, StringComparison.OrdinalIgnoreCase);
    }
}
