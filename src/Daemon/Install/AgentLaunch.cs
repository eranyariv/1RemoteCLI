using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Ipc;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// Starting the agent as the last act of an install, so the machine is reachable and
/// the tray icon is on screen when the install finishes.
/// <para>
/// Registering the logon task is not enough on its own: it takes effect at the next
/// logon, and until then the user has an install that produced no visible result and
/// a phone that lists no machines. The tray icon is also where sign-in state, the
/// session count and the settings live, so an agent nobody started is a product
/// nobody can see.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentLaunch
{
    /// <summary>
    /// Whether an agent is already serving this user.
    /// <para>
    /// The pipe, not the process list. Every wrapped session is a process called
    /// <c>1remote</c> too, so a name check would see <c>1remote claude</c> and decide
    /// the agent was up. The pipe is the thing the agent alone owns, and it is the
    /// same thing a second agent would fail to claim.
    /// </para>
    /// </summary>
    public static bool IsRunning() => File.Exists(@"\\.\pipe\" + AgentPipe.NameForCurrentUser());

    public static StepResult Start(string exePath) =>
        Start(
            IsRunning,
            () => TaskRegistration.IsRegistered(),
            TaskRegistration.RunNow,
            () => LaunchDetached(exePath));

    /// <summary>
    /// Which of the three cases this machine is in, with the things it does passed in
    /// so the choice can be tested without starting a real agent.
    /// </summary>
    internal static StepResult Start(
        Func<bool> isRunning,
        Func<bool> taskIsRegistered,
        Func<StepResult> runTask,
        Func<StepResult> launchDetached)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        ArgumentNullException.ThrowIfNull(taskIsRegistered);
        ArgumentNullException.ThrowIfNull(runTask);
        ArgumentNullException.ThrowIfNull(launchDetached);

        // An upgrade over a machine that was already set up, or a second `1remote
        // install`. Starting another would only produce a process that exits with "an
        // agent is already running", which reads like the install broke something.
        if (isRunning())
        {
            return StepResult.Success("The agent was already running.");
        }

        if (taskIsRegistered())
        {
            // Through the task rather than directly, so what runs now is exactly what
            // will run at every logon: same account, same environment, same arguments.
            // A machine where this works and the logon does not is a machine whose
            // install nobody can debug.
            StepResult viaTask = runTask();

            if (viaTask.Ok)
            {
                return viaTask;
            }

            // Registered but refused on demand. Rare, and not a reason to leave the
            // user with nothing when there is still a way to start it.
        }

        return launchDetached();
    }

    /// <summary>
    /// Starts <c>1remote agent</c> as a process of its own, for a machine where policy
    /// refused the task and the <c>Run</c> key fallback is what will start it at logon.
    /// <para>
    /// Says which of the two happened, because it cannot be told afterwards: a machine
    /// that quietly took this path looks exactly like one that took the task, right up
    /// until the agent does not come back at the next logon.
    /// </para>
    /// </summary>
    private static StepResult LaunchDetached(string exePath)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "agent",

                // Through the shell, which gives the agent a console of its own. The
                // alternatives are both wrong here: inheriting this one would print the
                // agent's output over whatever the installer is saying and would tie the
                // agent to a window the user is about to close, and redirecting its
                // output would stall the agent for good the moment the pipe nobody is
                // reading fills up. Hidden because the agent hides its own console at
                // startup anyway, and a window that flashes up and vanishes looks like a
                // fault.
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return process is null
                ? StepResult.Failure("Windows did not start the agent and gave no reason.")
                : StepResult.Success("Started the agent directly, because its logon task would not run it.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return StepResult.Failure($"Could not start the agent: {ex.Message}");
        }
    }
}
