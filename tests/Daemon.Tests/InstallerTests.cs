using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The install sequence.
/// <para>
/// The rule under test is that the machine ends up with exactly one thing starting the
/// agent at logon. Two would race for the same pipe and the loser would exit with "an
/// agent is already running", which reads like a bug and is nearly impossible to
/// diagnose from the tray.
/// </para>
/// </summary>
public sealed class InstallerTests
{
    private static Func<StepResult> Ok(string message = "did it") => () => StepResult.Success(message);

    private static Func<StepResult> Fails(string message = "refused") => () => StepResult.Failure(message);

    private static Func<StepResult> Never(Action onCall) => () =>
    {
        onCall();
        return StepResult.Success("should not happen");
    };

    [Fact]
    public void WhenTheTaskRegistersTheRunKeyIsRemovedRatherThanAdded()
    {
        bool added = false;
        bool removed = false;

        Installer.Install(
            registerTask: Ok("task registered"),
            removeRunKey: () =>
            {
                removed = true;
                return StepResult.Success("run key removed");
            },
            addRunKey: Never(() => added = true),
            installShortcuts: Ok());

        Assert.True(removed);
        Assert.False(added);
    }

    [Fact]
    public void WhenTheTaskIsRefusedTheRunKeyIsTheFallback()
    {
        // Group policy blocks task registration outright on some managed machines. A
        // worse trigger — one that cannot survive a logon without a desktop — still
        // beats no trigger at all.
        bool added = false;

        Installer.Install(
            registerTask: Fails("access is denied"),
            removeRunKey: Never(() => Assert.Fail("Removing the fallback we just needed.")),
            addRunKey: () =>
            {
                added = true;
                return StepResult.Success("run key added");
            },
            installShortcuts: Ok());

        Assert.True(added);
    }

    [Fact]
    public void AFailedStepDoesNotAbandonTheRest()
    {
        // Stopping early leaves a machine the user believes is set up, with no Start
        // menu entry to sign in from — the single worst outcome of this command.
        bool shortcuts = false;

        IReadOnlyList<StepResult> steps = Installer.Install(
            registerTask: Fails(),
            addRunKey: Fails(),
            removeRunKey: Ok(),
            installShortcuts: () =>
            {
                shortcuts = true;
                return StepResult.Success("shortcuts");
            });

        Assert.True(shortcuts);
        Assert.Equal(3, steps.Count);
    }

    [Fact]
    public void EveryStepIsReportedSoTheUserSeesWhatActuallyHappened()
    {
        IReadOnlyList<StepResult> steps = Installer.Install(
            registerTask: Ok("a"),
            removeRunKey: Ok("b"),
            addRunKey: Ok("unused"),
            installShortcuts: Ok("c"));

        Assert.Equal(["a", "b", "c"], steps.Select(step => step.Message));
    }

    [Fact]
    public void ASuccessfulInstallSaysTheAgentWillStartByItself()
    {
        string summary = Installer.Summarise([StepResult.Success("a"), StepResult.Success("b")], installing: true);

        Assert.Contains("log on", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialInstallSaysWhatToDoInsteadOfClaimingSuccess()
    {
        string summary = Installer.Summarise(
            [StepResult.Success("a"), StepResult.Failure("b"), StepResult.Failure("c")],
            installing: true);

        Assert.Contains("2 step", summary, StringComparison.Ordinal);

        // The user needs a way to keep working while it is broken.
        Assert.Contains("1remote agent", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallDoesNotClaimToHaveDeletedTheExecutable()
    {
        // It cannot: the running process is the file. Saying so avoids the user
        // hunting for a directory that was never removed.
        string summary = Installer.Summarise([StepResult.Success("a")], installing: false);

        Assert.Contains("still where you put it", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialUninstallDoesNotClaimTheMachineIsClean()
    {
        string summary = Installer.Summarise([StepResult.Failure("a")], installing: false);

        Assert.Contains("still present", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExecutablePathIsTheRealOneOnDisk()
    {
        // Not the assembly location: a single-file publish unpacks that to a temp
        // directory, and a task pointing there works once and is then gone.
        Assert.True(File.Exists(Installer.ExecutablePath));
    }

    [Fact]
    public void TheUserIdIsQualifiedByDomain()
    {
        // A bare user name matches a local account of the same name, which on a
        // domain-joined machine is a different person.
        Assert.Contains('\\', Installer.CurrentUserId);
    }
}
