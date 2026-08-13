using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>
/// The real desk console, put into VT passthrough mode for the duration of a session.
/// <para>
/// Passthrough means this process interprets nothing: the child's bytes go straight
/// to conhost, and the user's keystrokes go straight to the child. That is what makes
/// <c>1remote pwsh</c> indistinguishable from <c>pwsh</c>, including colours, TUI
/// applications and Ctrl+C handling inside the child.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocalTerminal : ILocalTerminal
{
    /// <summary>Used when there is no console at all — a service, or a redirected test host.</summary>
    private const int FallbackCols = 80;
    private const int FallbackRows = 24;

    private readonly IntPtr _inputHandle;
    private readonly IntPtr _outputHandle;
    private readonly int? _originalInputMode;
    private readonly int? _originalOutputMode;

    private int _restored;

    private WindowsLocalTerminal(
        IntPtr inputHandle,
        IntPtr outputHandle,
        int? originalInputMode,
        int? originalOutputMode,
        int cols,
        int rows)
    {
        _inputHandle = inputHandle;
        _outputHandle = outputHandle;
        _originalInputMode = originalInputMode;
        _originalOutputMode = originalOutputMode;
        Cols = cols;
        Rows = rows;

        Input = Console.OpenStandardInput();
        Output = Console.OpenStandardOutput();
    }

    public int Cols { get; }

    public int Rows { get; }

    public Stream Input { get; }

    public Stream Output { get; }

    /// <summary>
    /// Switches the console into passthrough mode, remembering what it was.
    /// <para>
    /// Restoration is also hooked to process exit and Ctrl+Break, because a wrapper
    /// that only restores on the happy path still leaves broken terminals behind —
    /// and that is precisely the failure a user cannot diagnose or undo themselves.
    /// </para>
    /// </summary>
    public static WindowsLocalTerminal Enter()
    {
        IntPtr input = ConsoleNativeMethods.GetStdHandle(ConsoleNativeMethods.STD_INPUT_HANDLE);
        IntPtr output = ConsoleNativeMethods.GetStdHandle(ConsoleNativeMethods.STD_OUTPUT_HANDLE);

        int? originalInput = null;
        int? originalOutput = null;

        if (ConsoleNativeMethods.GetConsoleMode(input, out int inMode))
        {
            originalInput = inMode;

            // Drop line buffering, local echo, and Windows' own Ctrl+C handling: the
            // child is the one that should see ^C, as a byte, like on any terminal.
            int raw = inMode
                & ~ConsoleNativeMethods.ENABLE_LINE_INPUT
                & ~ConsoleNativeMethods.ENABLE_ECHO_INPUT
                & ~ConsoleNativeMethods.ENABLE_PROCESSED_INPUT;

            raw |= ConsoleNativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT;
            ConsoleNativeMethods.SetConsoleMode(input, raw);
        }

        if (ConsoleNativeMethods.GetConsoleMode(output, out int outMode))
        {
            originalOutput = outMode;

            // DISABLE_NEWLINE_AUTO_RETURN keeps conhost from adding a carriage return
            // the child did not send, which would double-space full-screen output.
            int vt = outMode
                | ConsoleNativeMethods.ENABLE_PROCESSED_OUTPUT
                | ConsoleNativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING
                | ConsoleNativeMethods.DISABLE_NEWLINE_AUTO_RETURN;

            ConsoleNativeMethods.SetConsoleMode(output, vt);
        }

        (int cols, int rows) = MeasureWindow(output);

        var terminal = new WindowsLocalTerminal(input, output, originalInput, originalOutput, cols, rows);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => terminal.Restore();
        Console.CancelKeyPress += (_, _) => terminal.Restore();

        return terminal;
    }

    /// <summary>
    /// Reads the visible window, not the buffer. The buffer is usually far taller
    /// than the window, and sizing the pseudoconsole to it would make the child
    /// draw off-screen.
    /// </summary>
    private static (int Cols, int Rows) MeasureWindow(IntPtr outputHandle)
    {
        if (!ConsoleNativeMethods.GetConsoleScreenBufferInfo(outputHandle, out var info))
        {
            return (FallbackCols, FallbackRows);
        }

        int cols = info.srWindow.Right - info.srWindow.Left + 1;
        int rows = info.srWindow.Bottom - info.srWindow.Top + 1;

        return (cols > 0 ? cols : FallbackCols, rows > 0 ? rows : FallbackRows);
    }

    private void Restore()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0)
        {
            return;
        }

        if (_originalInputMode is int inMode)
        {
            ConsoleNativeMethods.SetConsoleMode(_inputHandle, inMode);
        }

        if (_originalOutputMode is int outMode)
        {
            ConsoleNativeMethods.SetConsoleMode(_outputHandle, outMode);
        }
    }

    public void Dispose()
    {
        Restore();
        Input.Dispose();
        Output.Dispose();
    }
}
