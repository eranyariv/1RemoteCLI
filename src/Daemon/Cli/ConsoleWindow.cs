using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Tray;

namespace OneRemoteCli.Daemon.Cli;

/// <summary>
/// The console window this process was given, and whether it is ours to hide.
/// <para>
/// The scheduled task runs <c>1remote agent</c> at logon. This is a console
/// application, so Windows creates a console for it, and that window then sits on the
/// desktop for the whole session: it cannot be closed without killing the agent, and
/// the tray icon already says everything it has to say. The task's <c>Hidden</c>
/// setting does not help — it hides the task from the scheduler's list, not the
/// window.
/// </para>
/// <para>
/// Hiding it unconditionally would be much worse than leaving it. Run from a
/// terminal, the console belongs to the shell, and hiding it would take the user's
/// terminal with it. <c>GetConsoleProcessList</c> tells the two cases apart: a
/// console with exactly one process attached was created for that process alone,
/// which is the scheduled task, or a double-click from Explorer.
/// </para>
/// <para>
/// The window is looked for repeatedly rather than once, because it does not
/// necessarily exist yet. <c>GetConsoleWindow</c> returns nothing until the console
/// host has created the window, and on a machine under load that can be after the
/// runtime has finished starting and reached this code. A single look that came too
/// early returned "no console", and the window it missed then stayed on screen for
/// the rest of the session — the exact fault this exists to prevent, made rare
/// enough to look like something else.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleWindow
{
    /// <summary>How long to keep looking, in total roughly two and a half seconds.</summary>
    internal const int Attempts = 25;

    internal const int PauseMilliseconds = 100;

    /// <summary>What to do about the console, on the evidence available so far.</summary>
    internal enum Verdict
    {
        /// <summary>Made for this process alone. Hide it.</summary>
        Hide,

        /// <summary>No window yet, and one may still be on its way.</summary>
        NotYet,

        /// <summary>Somebody else is on this console, or the question failed.</summary>
        LeaveAlone,
    }

    /// <summary>
    /// Whether a console carrying <paramref name="attachedProcesses"/> processes was
    /// made for this process alone.
    /// <para>
    /// Zero means <c>GetConsoleProcessList</c> failed, and anything above one means
    /// somebody else — a shell, almost always — is on the console and waiting for
    /// output. Both are answered the same way: leave the window alone.
    /// </para>
    /// </summary>
    internal static bool IsOursAlone(int attachedProcesses) => attachedProcesses == 1;

    /// <summary>
    /// The verdict on one look at the console.
    /// <para>
    /// Only the absence of a window is worth waiting on. A console that is already
    /// shared will not become ours later, so that answer is final the first time it
    /// is given — which also means the terminal a user is sitting in front of is
    /// judged once, immediately, and never reconsidered by anything that runs after.
    /// </para>
    /// </summary>
    internal static Verdict Decide(bool hasWindow, int attachedProcesses) =>
        !hasWindow ? Verdict.NotYet
            : IsOursAlone(attachedProcesses) ? Verdict.Hide
                : Verdict.LeaveAlone;

    /// <summary>
    /// Looks for the console window until it appears, and hides it if it turns out to
    /// be ours. Gives up after <paramref name="attempts"/> looks, which is the normal
    /// outcome for a process that has no console of its own to find.
    /// </summary>
    internal static Verdict HideWhenReady(
        Func<bool> hasWindow,
        Func<int> attachedProcesses,
        Action hide,
        Action pause,
        int attempts)
    {
        ArgumentNullException.ThrowIfNull(hasWindow);
        ArgumentNullException.ThrowIfNull(attachedProcesses);
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(pause);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Verdict verdict = Decide(hasWindow(), attachedProcesses());

            if (verdict == Verdict.Hide)
            {
                hide();

                return verdict;
            }

            if (verdict == Verdict.LeaveAlone)
            {
                return verdict;
            }

            pause();
        }

        return Verdict.NotYet;
    }

    /// <summary>
    /// Hides the console window if this process is the only thing attached to it.
    /// Does nothing when a shell is sharing it, when the count cannot be read, or when
    /// no window ever appears.
    /// <para>
    /// On a thread of its own, because the agent must not wait on a window: the point
    /// of starting it is that the machine becomes reachable, and that has to happen at
    /// the same speed whether or not there is a console to tidy away.
    /// </para>
    /// </summary>
    internal static void HideIfOurs()
    {
        var thread = new Thread(static () => HideWhenReady(
            static () => GetConsoleWindow() != IntPtr.Zero,
            CountAttached,
            HideNow,
            static () => Thread.Sleep(PauseMilliseconds),
            Attempts))
        {
            IsBackground = true,
            Name = "console-window",
        };

        thread.Start();
    }

    /// <summary>
    /// Asking for more than one identifier so a shared console is recognised as
    /// shared. The count comes back whether or not the buffer was big enough.
    /// </summary>
    private static int CountAttached()
    {
        uint[] processes = new uint[4];

        return GetConsoleProcessList(processes, processes.Length);
    }

    private static void HideNow()
    {
        IntPtr window = GetConsoleWindow();

        if (window == IntPtr.Zero)
        {
            return;
        }

        // The first ShowWindow a process makes is overruled by whatever its launcher
        // put in STARTUPINFO, so the command is given twice and the second is the one
        // that counts.
        NativeMethods.ShowWindow(window, NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(window, NativeMethods.SW_HIDE);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetConsoleProcessList(uint[] processList, int count);
}
