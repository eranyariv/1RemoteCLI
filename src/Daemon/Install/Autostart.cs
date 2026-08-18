using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// Whether the agent starts when the user signs in to Windows, and the one rule that
/// governs it: exactly one thing may do the starting.
/// <para>
/// Two triggers would race at logon and the loser exits with "an agent is already
/// running", which reads like a bug and is close to undiagnosable from the tray. So
/// the Scheduled Task is preferred and the Run key exists only as the fallback for a
/// machine where policy refused the task outright.
/// </para>
/// <para>
/// Split out of <see cref="Installer"/> because there are now two callers: the
/// install command, which does this among several other things, and the settings
/// window's checkbox, which does only this. A checkbox with its own copy of the rule
/// would be a second answer to "what starts the agent", and the two would drift.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Autostart
{
    /// <summary>
    /// What is actually registered, asked afresh.
    /// <para>
    /// Never a remembered preference. The task can be deleted from Task Scheduler, or
    /// the Run entry disabled in Task Manager's Startup tab, without this process
    /// hearing about it — so a checkbox drawn from anything but the live answer would
    /// confidently show the opposite of the truth.
    /// </para>
    /// <para>
    /// The fallback counts as on. From the user's point of view the question is "does
    /// it start by itself", and on a machine where policy refused the task the Run key
    /// is the yes.
    /// </para>
    /// </summary>
    public static bool IsEnabled() => TaskRegistration.IsRegistered() || RunKey.IsRegistered();

    public static IReadOnlyList<StepResult> Enable(string exePath, string userId) =>
        Enable(
            () => TaskRegistration.Register(exePath, userId),
            RunKey.Remove,
            () => RunKey.Register(exePath));

    /// <summary>
    /// The rule itself, with the things it does passed in, so it can be tested without
    /// registering a real task on the machine running the tests.
    /// </summary>
    internal static IReadOnlyList<StepResult> Enable(
        Func<StepResult> registerTask,
        Func<StepResult> removeRunKey,
        Func<StepResult> addRunKey)
    {
        StepResult task = Step(registerTask);

        return
        [
            task,

            // Only ever one autostart. On success the fallback is removed; on failure it
            // is the only trigger left, and a worse trigger beats no trigger.
            task.Ok ? Step(removeRunKey) : Step(addRunKey),
        ];
    }

    public static IReadOnlyList<StepResult> Disable() =>
        Disable(() => TaskRegistration.Remove(), RunKey.Remove);

    /// <summary>
    /// Removes both, unconditionally.
    /// <para>
    /// Both, even though only one should ever be registered: a machine that somehow has
    /// the pair must not be left with the other one still starting the agent after the
    /// user has said no.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<StepResult> Disable(Func<StepResult> removeTask, Func<StepResult> removeRunKey) =>
        [Step(removeTask), Step(removeRunKey)];

    /// <summary>
    /// One sentence about what a set of steps achieved, for a caller with no room to
    /// print them one by one.
    /// </summary>
    public static string Summarise(IReadOnlyList<StepResult> steps, bool enabling)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.All(step => step.Ok))
        {
            return enabling
                ? "1remote will start when you sign in to Windows."
                : "1remote will no longer start by itself.";
        }

        // The first failure rather than a count. There are only ever two steps, and the
        // reason the machine refused is the whole message.
        string reason = steps.First(step => !step.Ok).Message;

        return enabling
            ? $"Could not arrange for the agent to start at logon. {reason}"
            : $"Could not stop the agent starting at logon. {reason}";
    }

    /// <summary>
    /// Runs one step, turning anything it throws into a failure rather than letting it
    /// escape. See <see cref="Installer"/> for why every one of these is caught.
    /// </summary>
    private static StepResult Step(Func<StepResult> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            return StepResult.Failure(ex.Message);
        }
    }
}
