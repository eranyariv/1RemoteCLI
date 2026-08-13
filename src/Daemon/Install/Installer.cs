using System.Diagnostics;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// <c>1remote install</c> and <c>1remote uninstall</c>.
/// <para>
/// Every step reports rather than throws, and one failing step does not abandon the
/// rest. A half-installed machine is the worst outcome here: the user believes it is
/// set up, nothing happens at logon, and the only evidence is a message that scrolled
/// past. So the sequence always runs to the end and always prints what it managed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    /// <summary>
    /// Where this executable actually is.
    /// <para>
    /// From the process, not from the assembly: a single-file publish unpacks the
    /// managed assembly to a temp directory, and a task pointing there would work
    /// once and then be gone.
    /// </para>
    /// </summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine this executable's path.");

    /// <summary>The account the task should run as.</summary>
    public static string CurrentUserId =>
        string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.UserName
            : $"{Environment.UserDomainName}\\{Environment.UserName}";

    public static IReadOnlyList<StepResult> Install(string exePath, string userId) =>
        Install(
            () => TaskRegistration.Register(exePath, userId),
            RunKey.Remove,
            () => RunKey.Register(exePath),
            () => StartMenu.Install(exePath));

    /// <summary>
    /// The install sequence, with the four things it does passed in.
    /// <para>
    /// Split out so the one rule that matters here can be tested without registering a
    /// real task on the machine running the tests.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<StepResult> Install(
        Func<StepResult> registerTask,
        Func<StepResult> removeRunKey,
        Func<StepResult> addRunKey,
        Func<StepResult> installShortcuts)
    {
        List<StepResult> steps = [];

        StepResult task = registerTask();
        steps.Add(task);

        if (task.Ok)
        {
            // Only ever one autostart. Both together would race at logon, and the loser
            // exits with "an agent is already running" — which reads like a bug.
            steps.Add(removeRunKey());
        }
        else
        {
            // Task registration is refused outright by policy on some managed machines.
            // A worse trigger beats no trigger.
            steps.Add(addRunKey());
        }

        steps.Add(installShortcuts());

        return steps;
    }

    public static IReadOnlyList<StepResult> Uninstall() =>
    [
        TaskRegistration.Remove(),
        RunKey.Remove(),
        StartMenu.Remove(),
    ];

    /// <summary>
    /// What to tell the user once the steps have run.
    /// <para>
    /// Separated from the doing so the wording is testable, and so the summary can say
    /// something useful about a partial install rather than just listing what happened.
    /// </para>
    /// </summary>
    public static string Summarise(IReadOnlyList<StepResult> steps, bool installing)
    {
        ArgumentNullException.ThrowIfNull(steps);

        int failed = steps.Count(step => !step.Ok);

        if (failed == 0)
        {
            return installing
                ? "1remote is installed. The agent starts automatically when you log on."
                : "1remote is uninstalled. The executable itself is still where you put it.";
        }

        return installing
            ? $"{failed} step(s) failed. The agent may not start automatically; run '1remote agent' by hand until this is fixed."
            : $"{failed} step(s) failed. Some of 1remote's installation is still present.";
    }
}
