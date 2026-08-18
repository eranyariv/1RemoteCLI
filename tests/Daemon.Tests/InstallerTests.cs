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
            installShortcuts: Ok(),
            addToPath: Ok(),
            startAgent: Ok());

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
            installShortcuts: Ok(),
            addToPath: Ok(),
            startAgent: Ok());

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
            },
            addToPath: Ok(),
            startAgent: Ok());

        Assert.True(shortcuts);
        Assert.Equal(5, steps.Count);
    }

    /// <summary>
    /// On a machine where policy refused every autostart, typing '1remote agent' by hand
    /// is the only repair left — so the PATH entry that makes that possible cannot be
    /// conditional on the autostart having worked.
    /// </summary>
    [Fact]
    public void ThePathEntryIsAddedEvenWhenNothingWillStartTheAgentByItself()
    {
        bool onPath = false;

        Installer.Install(
            registerTask: Fails(),
            removeRunKey: Ok(),
            addRunKey: Fails(),
            installShortcuts: Fails(),
            addToPath: () =>
            {
                onPath = true;
                return StepResult.Success("path");
            },
            startAgent: Ok());

        Assert.True(onPath);
    }

    [Fact]
    public void EveryStepIsReportedSoTheUserSeesWhatActuallyHappened()
    {
        IReadOnlyList<StepResult> steps = Installer.Install(
            registerTask: Ok("a"),
            removeRunKey: Ok("b"),
            addRunKey: Ok("unused"),
            installShortcuts: Ok("c"),
            addToPath: Ok("d"),
            startAgent: Ok("e"));

        Assert.Equal(["a", "b", "c", "d", "e"], steps.Select(step => step.Message));
    }

    /// <summary>
    /// Issue #100: registering the logon task only makes the agent appear at the next
    /// logon, so an install that stopped there left the user with no tray icon, no
    /// relay and nothing to show that any of it worked.
    /// </summary>
    [Fact]
    public void TheAgentIsStartedRatherThanLeftUntilTheNextLogon()
    {
        bool started = false;

        Installer.Install(
            registerTask: Ok(),
            removeRunKey: Ok(),
            addRunKey: Ok(),
            installShortcuts: Ok(),
            addToPath: Ok(),
            startAgent: () =>
            {
                started = true;
                return StepResult.Success("started");
            });

        Assert.True(started);
    }

    /// <summary>
    /// An agent running now is what makes the machine reachable today, whatever the
    /// logon trigger managed — so on the machine that needs it most, the one where
    /// policy refused everything, it must still be attempted.
    /// </summary>
    [Fact]
    public void TheAgentIsStartedEvenWhenEveryEarlierStepFailed()
    {
        bool started = false;

        Installer.Install(
            registerTask: Fails(),
            removeRunKey: Ok(),
            addRunKey: Fails(),
            installShortcuts: Fails(),
            addToPath: Fails(),
            startAgent: () =>
            {
                started = true;
                return StepResult.Success("started");
            });

        Assert.True(started);
    }

    [Fact]
    public void AStepThatThrowsBecomesAFailedStepAndTheRestStillRun()
    {
        // Issue #72: a step threw a type the step itself did not catch, which abandoned
        // every later step while keeping the effects of the earlier ones. A half-done
        // install is worse than a reported failure, because nothing says which half.
        IReadOnlyList<StepResult> steps = Installer.Install(
            registerTask: Ok("a"),
            removeRunKey: Ok("b"),
            addRunKey: Ok("unused"),
            installShortcuts: () => throw new NotSupportedException("no COM for you"),
            addToPath: Ok("d"),
            startAgent: Ok("e"));

        Assert.Equal(["a", "b", "no COM for you", "d", "e"], steps.Select(step => step.Message));
        Assert.False(steps[2].Ok);
        Assert.True(steps[3].Ok);
    }

    [Fact]
    public void ASuccessfulInstallSaysTheAgentIsRunningAndWillStartByItself()
    {
        string summary = Installer.Summarise([StepResult.Success("a"), StepResult.Success("b")], installing: true);

        // Both halves matter: the tray icon is the only evidence the user has that any
        // of this worked, and the logon trigger is why they never have to do it again.
        Assert.Contains("notification area", summary, StringComparison.Ordinal);
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
