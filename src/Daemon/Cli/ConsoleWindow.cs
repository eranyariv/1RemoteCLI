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
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleWindow
{
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
    /// Hides the console window if this process is the only thing attached to it.
    /// Does nothing when there is no console, when the call fails, or when a shell is
    /// sharing it.
    /// </summary>
    internal static void HideIfOurs()
    {
        IntPtr window = GetConsoleWindow();

        if (window == IntPtr.Zero)
        {
            return;
        }

        // Asking for more than one identifier so a shared console is recognised as
        // shared. The count comes back whether or not the buffer was big enough.
        uint[] processes = new uint[4];

        if (!IsOursAlone(GetConsoleProcessList(processes, processes.Length)))
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
