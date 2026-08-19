using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OneRemoteCli.Daemon.Cli;

/// <summary>
/// The console this process was given, and how the agent gets rid of one.
/// <para>
/// The scheduled task runs <c>1remote agent</c> at logon. This is a console
/// application, so Windows creates a console for it before any of our code runs, and
/// on Windows 11 that means a Windows Terminal window sitting on the desktop for the
/// whole session. It cannot be closed without killing the agent, and the tray icon
/// already says everything it has to say.
/// </para>
/// <para>
/// Three answers were tried against that window before this one, and the first two
/// were wrong in ways worth recording, because each looks obviously right until it is
/// measured (issue #106):
/// </para>
/// <para>
/// Hiding it cannot work. <c>GetConsoleWindow</c> returns an invisible
/// <c>PseudoConsoleWindow</c> belonging to this process, while the window a user can
/// see belongs to <c>WindowsTerminal.exe</c> — a different process entirely. Every
/// <c>ShowWindow(SW_HIDE)</c> we ever shipped hid the placeholder and left the real
/// window alone. Polling for it harder, which is what the previous version of this
/// class did, only changed how reliably we hid the wrong thing.
/// </para>
/// <para>
/// Freeing it does not work either. <c>FreeConsole</c> needs no window, so it looked
/// like the answer, but measured on both machines the terminal window outlives the
/// call.
/// </para>
/// <para>
/// Building for the windows subsystem does remove the window — and breaks the command
/// line, since the same executable is also the CLI. Measured: nothing printed reaches
/// the terminal, the shell does not wait for the process, the exit code is lost, and
/// redirection to a file captures nothing.
/// </para>
/// <para>
/// What is left, and what this does: start a second copy of ourselves with
/// <c>DETACHED_PROCESS</c> — which is not <c>CreateNoWindow</c>, and means no console
/// is created for it at all — and exit. The window belongs to the first process and
/// goes when it does. Measured at around a tenth of a second on screen, against a
/// window that otherwise stays for the session.
/// </para>
/// <para>
/// The copy has no standard handles at all — <c>DETACHED_PROCESS</c> leaves them NULL
/// rather than merely closed — so what the agent does on startup was measured against
/// that: <c>Console.Out</c> and <c>Console.Error</c> writes are safe, because .NET
/// hands back a sink that goes nowhere, and registering <c>Console.CancelKeyPress</c>
/// is safe too. Nothing on the agent path asks for a window size, which is the one
/// thing that does throw. So no logging had to change for this.
/// </para>
/// <para>
/// Handing off unconditionally would be much worse than leaving it alone. Run from a
/// terminal, the console belongs to the shell, and a user who typed
/// <c>1remote agent</c> to watch it wants to see it and to stop it with Ctrl+C.
/// <c>GetConsoleProcessList</c> tells the cases apart, and it also identifies the
/// detached copy, which has no console and so counts zero — so there is no marker to
/// pass down and no way to recurse.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AgentConsole
{
    /// <summary>What to do about the console this process was given.</summary>
    internal enum Verdict
    {
        /// <summary>
        /// A console made for this process alone, which means the scheduled task or a
        /// double-click. Start a copy without one and leave.
        /// </summary>
        HandOff,

        /// <summary>
        /// No console, so this is already the detached copy; or a console shared with
        /// a shell, whose output someone is reading. Both mean carry on here.
        /// </summary>
        StayHere,
    }

    /// <summary>
    /// What to do, given how many processes are attached to this console.
    /// <para>
    /// Exactly one is this process alone with a console of its own. Zero is no console
    /// at all — the detached copy, and also what a failed call reports, which wants the
    /// same answer. More than one is a shell sharing it.
    /// </para>
    /// </summary>
    internal static Verdict Decide(int attachedProcesses) =>
        attachedProcesses == 1 ? Verdict.HandOff : Verdict.StayHere;

    /// <summary>
    /// Starts a console-free copy of this process and reports whether the caller should
    /// now exit.
    /// <para>
    /// False for every outcome that is not a completed handoff, including a
    /// <c>CreateProcess</c> that fails: an agent running under a stray window is a
    /// blemish, and no agent at all is a broken machine.
    /// </para>
    /// </summary>
    internal static bool HandOffIfOurs(string[] args, Func<int> attachedProcesses, Func<string[], bool> start)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(attachedProcesses);
        ArgumentNullException.ThrowIfNull(start);

        return Decide(attachedProcesses()) == Verdict.HandOff && start(args);
    }

    /// <summary>
    /// Starts a console-free copy of this process and reports whether the caller should
    /// now exit.
    /// </summary>
    internal static bool HandOffIfOurs(string[] args) =>
        HandOffIfOurs(args, CountAttached, StartDetached);

    /// <summary>
    /// Asking for more than one identifier so a shared console is recognised as
    /// shared. The count comes back whether or not the buffer was big enough.
    /// </summary>
    private static int CountAttached()
    {
        uint[] processes = new uint[4];

        return GetConsoleProcessList(processes, processes.Length);
    }

    /// <summary>
    /// The same executable, the same arguments, no console.
    /// <para>
    /// <c>Process.Start</c> cannot express this: its <c>CreateNoWindow</c> asks for
    /// <c>CREATE_NO_WINDOW</c>, which still creates a console and merely asks for it to
    /// be invisible — a request the Windows Terminal host does not honour, which is the
    /// whole problem. So this is <c>CreateProcess</c> directly.
    /// </para>
    /// </summary>
    private static bool StartDetached(string[] args)
    {
        string? exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        var startup = new StartupInfo();
        startup.Size = Marshal.SizeOf<StartupInfo>();

        bool started = CreateProcess(
            exe,
            // A StringBuilder rather than a string: CreateProcessW is documented to be
            // allowed to write into this buffer, and a managed string is passed by
            // pinning rather than by copy, so it would be writing into our own memory.
            new StringBuilder(CommandLineFor(exe, args)),
            IntPtr.Zero,
            IntPtr.Zero,
            inheritHandles: false,
            DetachedProcess,
            IntPtr.Zero,
            null,
            ref startup,
            out ProcessInformation information);

        if (started)
        {
            // Nothing here waits for the copy, so the only thing these handles would do
            // is keep its process record alive after it exits.
            CloseHandle(information.Process);
            CloseHandle(information.Thread);
        }

        return started;
    }

    /// <summary>
    /// The command line for the copy: the executable, then whatever this process was
    /// given.
    /// <para>
    /// The executable is always quoted, because it is installed under a path with a
    /// space in it often enough that the unquoted form would start something else or
    /// nothing at all. Arguments are quoted only when they need it, so that what shows
    /// up in Task Manager is readable.
    /// </para>
    /// <para>
    /// Passing the arguments through rather than hard-coding <c>agent</c> means a flag
    /// added to the agent later reaches the process that actually does the work,
    /// instead of being quietly dropped by the handoff.
    /// </para>
    /// </summary>
    internal static string CommandLineFor(string executable, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        IEnumerable<string> parts = new[] { Quote(executable) }.Concat(arguments.Select(QuoteIfNeeded));

        return string.Join(' ', parts);
    }

    private static string Quote(string value) => '"' + value + '"';

    private static string QuoteIfNeeded(string value) =>
        value.Length > 0 && !value.Contains(' ', StringComparison.Ordinal) ? value : Quote(value);

    private const uint DetachedProcess = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetConsoleProcessList(uint[] processList, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
