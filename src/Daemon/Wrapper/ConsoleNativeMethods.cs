using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>
/// Console mode and window-size interop for the desk terminal the wrapper runs in.
/// Separate from the ConPTY P/Invokes because these act on the console we were
/// *given*, not the one we create.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ConsoleNativeMethods
{
    internal const int STD_INPUT_HANDLE = -10;
    internal const int STD_OUTPUT_HANDLE = -11;

    internal const int ENABLE_PROCESSED_INPUT = 0x0001;
    internal const int ENABLE_LINE_INPUT = 0x0002;
    internal const int ENABLE_ECHO_INPUT = 0x0004;
    internal const int ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    internal const int ENABLE_PROCESSED_OUTPUT = 0x0001;
    internal const int ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    internal const int DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleScreenBufferInfo(
        IntPtr hConsoleOutput,
        out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);
}
