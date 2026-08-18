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
    private readonly uint? _originalOutputCodePage;
    private readonly uint? _originalInputCodePage;

    private int _restored;

    private WindowsLocalTerminal(
        IntPtr inputHandle,
        IntPtr outputHandle,
        int? originalInputMode,
        int? originalOutputMode,
        uint? originalOutputCodePage,
        uint? originalInputCodePage,
        int cols,
        int rows)
    {
        _inputHandle = inputHandle;
        _outputHandle = outputHandle;
        _originalInputMode = originalInputMode;
        _originalOutputMode = originalOutputMode;
        _originalOutputCodePage = originalOutputCodePage;
        _originalInputCodePage = originalInputCodePage;
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

        (uint? originalOutputCodePage, uint? originalInputCodePage) = EnterUtf8();

        var terminal = new WindowsLocalTerminal(
            input,
            output,
            originalInput,
            originalOutput,
            originalOutputCodePage,
            originalInputCodePage,
            cols,
            rows);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => terminal.Restore();
        Console.CancelKeyPress += (_, _) => terminal.Restore();

        return terminal;
    }

    /// <summary>
    /// Puts the console on UTF-8, and reports what it was so it can be put back.
    /// <para>
    /// The wrapper hands the child's bytes to conhost untouched, and every CLI worth
    /// wrapping speaks UTF-8. What those bytes look like on screen is decided by the
    /// console's code page, which this process did not choose and cannot assume: a
    /// terminal the user configured is usually already on 65001, but a console spawned
    /// fresh from a desktop shortcut gets the system OEM page — 437 or 850 — and every
    /// multi-byte character arrives as one glyph per byte. That is why <c>│</c> shows
    /// up as <c>Γöé</c>, and why the bug appeared only once shortcuts could be wrapped
    /// (issue #66): until then every session started in a console somebody had already
    /// set up.
    /// </para>
    /// <para>
    /// Set before the standard streams are opened, because .NET caches the encoding
    /// when it first opens them.
    /// </para>
    /// </summary>
    private static (uint? Output, uint? Input) EnterUtf8()
    {
        uint? previousOutput = CodePageToRestore(ConsoleNativeMethods.GetConsoleOutputCP());

        if (previousOutput is not null
            && !ConsoleNativeMethods.SetConsoleOutputCP(ConsoleNativeMethods.CP_UTF8))
        {
            previousOutput = null;
        }

        // Input too, and separately: they are two settings, and a console left with a
        // UTF-8 screen but an OEM keyboard mangles anything typed that is not ASCII.
        uint? previousInput = CodePageToRestore(ConsoleNativeMethods.GetConsoleCP());

        if (previousInput is not null
            && !ConsoleNativeMethods.SetConsoleCP(ConsoleNativeMethods.CP_UTF8))
        {
            previousInput = null;
        }

        return (previousOutput, previousInput);
    }

    /// <summary>
    /// Given the code page a console is on, what would have to be put back afterwards —
    /// and so, by being null, whether to touch it at all.
    /// <para>
    /// Zero means there is no console: output is redirected to a file or a pipe, where
    /// the code page is meaningless and setting it would fail anyway. 65001 means the
    /// user, or their terminal, already chose UTF-8; changing nothing means restoring
    /// nothing, which matters because a wrapper that "restores" a console it never
    /// altered is a wrapper that can leave one worse than it found it.
    /// </para>
    /// </summary>
    internal static uint? CodePageToRestore(uint current) =>
        current is 0 or ConsoleNativeMethods.CP_UTF8 ? null : current;

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

        // Only ever set when it was changed, so this cannot put a console the user had
        // deliberately on UTF-8 back onto something else. Leaving a terminal altered
        // after the wrapper exits is the same failure as leaving the modes altered:
        // everything typed afterwards is wrong, and nothing says why.
        if (_originalOutputCodePage is uint outCodePage)
        {
            ConsoleNativeMethods.SetConsoleOutputCP(outCodePage);
        }

        if (_originalInputCodePage is uint inCodePage)
        {
            ConsoleNativeMethods.SetConsoleCP(inCodePage);
        }
    }

    public void Dispose()
    {
        Restore();
        Input.Dispose();
        Output.Dispose();
    }
}
