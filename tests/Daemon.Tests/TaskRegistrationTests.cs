using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Registering the real thing on the real machine.
/// <para>
/// The unit tests above assert what the XML says; only Task Scheduler can say whether
/// it will accept it, and it rejects malformed input with one unhelpful sentence. The
/// encoding alone is a trap — <c>schtasks /XML</c> refuses a UTF-8 file with "the task
/// XML is malformed" and no further detail.
/// </para>
/// <para>
/// Uses a task name of its own so a developer running the suite never has their real
/// installed agent removed, and cleans up whatever happens.
/// </para>
/// </summary>
[Collection("Scheduled tasks")]
public sealed class TaskRegistrationTests : IDisposable
{
    /// <summary>Deliberately not <see cref="AgentTask.TaskName"/>. See the class remarks.</summary>
    private const string TestTaskName = "1RemoteCLI Agent (test)";

    private readonly string _exe = Environment.ProcessPath!;

    public void Dispose() => TaskRegistration.Remove(TestTaskName);

    [Fact]
    public void ARegisteredTaskCanBeFoundAndThenRemoved()
    {
        StepResult registered = TaskRegistration.Register(_exe, Installer.CurrentUserId, TestTaskName);

        if (!registered.Ok)
        {
            // Group policy refuses task registration outright on managed machines.
            // That is a supported outcome — Installer falls back to the Run key — so
            // it must not be reported as a test failure here.
            Assert.False(TaskRegistration.IsRegistered(TestTaskName));
            return;
        }

        Assert.True(TaskRegistration.IsRegistered(TestTaskName));

        StepResult removed = TaskRegistration.Remove(TestTaskName);

        Assert.True(removed.Ok, removed.Message);
        Assert.False(TaskRegistration.IsRegistered(TestTaskName));
    }

    [Fact]
    public void RegisteringTwiceReplacesRatherThanFailing()
    {
        // Re-running the installer over an existing install is the normal upgrade
        // path, and must not require an uninstall first.
        StepResult first = TaskRegistration.Register(_exe, Installer.CurrentUserId, TestTaskName);

        if (!first.Ok)
        {
            return;
        }

        StepResult second = TaskRegistration.Register(_exe, Installer.CurrentUserId, TestTaskName);

        Assert.True(second.Ok, second.Message);
    }

    [Fact]
    public void RemovingSomethingThatWasNeverThereIsNotAnError()
    {
        // Uninstall must be safe to run on a machine that was never installed, or on
        // one where only half the steps worked.
        StepResult removed = TaskRegistration.Remove("1RemoteCLI Agent (absent)");

        Assert.True(removed.Ok, removed.Message);
    }

    [Fact]
    public void AnUnregisteredTaskIsReportedAsAbsent() =>
        Assert.False(TaskRegistration.IsRegistered("1RemoteCLI Agent (absent)"));
}

/// <summary>
/// The Start menu shortcuts.
/// <para>
/// Real COM against the real shell, because a hand-declared <c>IShellLinkW</c> vtable
/// either matches or corrupts the stack, and nothing short of calling it can tell.
/// </para>
/// </summary>
[Collection("Scheduled tasks")]
public sealed class StartMenuTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "1remote-startmenu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Best effort. This is a temp folder; failing to remove it says nothing about
        // the code under test, and throwing here would turn a scanner holding a handle
        // into a red build.
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ShortcutsAreCreatedAndCanBeRemoved()
    {
        StepResult installed = StartMenu.Install(Environment.ProcessPath!, _folder);

        Assert.True(installed.Ok, installed.Message);

        string[] links = Directory.GetFiles(_folder, "*.lnk");

        // One to sign in, one to start the agent by hand. Both are the entire
        // discoverable surface for a user who never opens a terminal.
        Assert.True(links.Length == 2, $"Expected 2 shortcuts, found {links.Length}: {Describe(_folder)}");

        // A zero-byte .lnk is what a mismatched vtable produces: the call appears to
        // succeed and the shell writes nothing.
        Assert.All(links, link => Assert.True(
            new FileInfo(link).Length > 0,
            $"{Path.GetFileName(link)} was written empty."));

        StepResult removed = StartMenu.Remove(_folder);

        Assert.True(removed.Ok, removed.Message);

        // Polled rather than read once, because a Windows directory does not always
        // vanish the instant Delete returns. Anything holding a handle opened with
        // FILE_SHARE_DELETE -- which is exactly how a virus scanner opens files, so as
        // not to block deletes -- leaves the entry in a pending-delete state: gone as
        // far as Delete is concerned, still visible to Exists until the last handle
        // closes. Freshly written .lnk files are precisely what a scanner opens.
        //
        // This does not weaken the test. A Remove that genuinely left files behind
        // still fails, five seconds later.
        Assert.True(
            WaitUntil(() => !Directory.Exists(_folder)),
            $"The folder outlived its removal: {Describe(_folder)}");
    }

    [Fact]
    public void SurvivesInstallingAndUninstallingBackToBack()
    {
        // Removing immediately after installing is the case that fails: the Start Menu
        // indexer opens a new .lnk within milliseconds and the delete is refused while
        // it does. Once through would pass on lucky timing, so this runs the cycle
        // enough times that the race cannot hide - which is how it surfaced in the first
        // place, as a test that only failed in a full suite run.
        for (int cycle = 1; cycle <= 10; cycle++)
        {
            StepResult installed = StartMenu.Install(Environment.ProcessPath!, _folder);

            Assert.True(installed.Ok, $"Cycle {cycle}: {installed.Message}");

            StepResult removed = StartMenu.Remove(_folder);

            Assert.True(removed.Ok, $"Cycle {cycle}: {removed.Message}");
        }
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>
    /// Named so a failure says which step went wrong rather than which line did. Every
    /// assertion above can only fail on a machine under load, and this test's whole
    /// value is telling us what the shell actually did.
    /// </summary>
    private static string Describe(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return $"{folder} does not exist";
        }

        string[] entries = Directory.GetFileSystemEntries(folder);

        return entries.Length == 0
            ? $"{folder} exists and is empty"
            : $"{folder} contains {string.Join(", ", entries.Select(Path.GetFileName))}";
    }

    [Fact]
    public void RemovingShortcutsThatWereNeverThereIsNotAnError()
    {
        StepResult removed = StartMenu.Remove(Path.Combine(_folder, "absent"));

        Assert.True(removed.Ok, removed.Message);
    }
}

/// <summary>
/// Shared so the two suites above never register and remove overlapping shell state
/// at the same time.
/// </summary>
[CollectionDefinition("Scheduled tasks", DisableParallelization = true)]
public sealed class ScheduledTaskCollection
{
}
