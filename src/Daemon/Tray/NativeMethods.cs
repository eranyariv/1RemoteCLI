using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>
/// The Win32 surface the tray icon is built on.
/// <para>
/// Hand-declared rather than taken from Windows Forms, because <c>NotifyIcon</c> was
/// the only thing the agent used it for and it cost 40 MB of Windows Desktop runtime
/// in every download (issue #46). The shell API underneath is small — one struct, one
/// function, a window class and a popup menu — and none of it has changed since
/// Windows 2000.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const int WM_NULL = 0x0000;
    internal const int WM_DESTROY = 0x0002;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_SETFONT = 0x0030;
    internal const int WM_COMMAND = 0x0111;
    internal const int WM_TIMER = 0x0113;
    internal const int WM_CTLCOLORSTATIC = 0x0138;
    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_CONTEXTMENU = 0x007B;
    internal const int WM_APP = 0x8000;

    /// <summary>The shell's notification callback. Anything in the WM_APP range is ours.</summary>
    internal const int WM_TRAYICON = WM_APP + 1;

    /// <summary>Posted from other threads to ask the icon's thread to redraw.</summary>
    internal const int WM_TRAY_UPDATE = WM_APP + 2;

    /// <summary>Posted from other threads to ask the icon's thread to shut down.</summary>
    internal const int WM_TRAY_QUIT = WM_APP + 3;

    /// <summary>
    /// Posted from other threads to ask the icon's thread to open the settings window.
    /// A window has to be created on the thread that pumps its messages, and every
    /// other menu action deliberately runs off that thread.
    /// </summary>
    internal const int WM_TRAY_SETTINGS = WM_APP + 4;

    internal const int NIM_ADD = 0x00000000;
    internal const int NIM_MODIFY = 0x00000001;
    internal const int NIM_DELETE = 0x00000002;
    internal const int NIM_SETVERSION = 0x00000004;

    internal const int NIF_MESSAGE = 0x00000001;
    internal const int NIF_ICON = 0x00000002;
    internal const int NIF_TIP = 0x00000004;

    /// <summary>
    /// Required under version 4: the shell stops drawing the standard tooltip unless
    /// asked, on the assumption that an app on the new behaviour wants to draw its own.
    /// Without this the hover text -- the agent's entire diagnostic surface -- silently
    /// disappears.
    /// </summary>
    internal const int NIF_SHOWTIP = 0x00000080;

    /// <summary>
    /// Version 4 behaviour, which is what makes the shell send <c>WM_CONTEXTMENU</c>
    /// with screen coordinates in <c>lParam</c> instead of leaving us to ask for the
    /// cursor position after the fact — the version that gets the menu in the right
    /// place when the tray is opened from the keyboard.
    /// </summary>
    internal const int NOTIFYICON_VERSION_4 = 4;

    internal const int TPM_RIGHTBUTTON = 0x0002;
    internal const int TPM_BOTTOMALIGN = 0x0020;

    /// <summary>
    /// Hands the chosen command back as the return value instead of posting
    /// <c>WM_COMMAND</c>. It keeps the dispatch next to the menu that was built, which
    /// is what lets the menu be rebuilt from scratch on every click rather than kept
    /// in sync item by item.
    /// </summary>
    internal const int TPM_RETURNCMD = 0x0100;

    internal const int MF_STRING = 0x00000000;
    internal const int MF_SEPARATOR = 0x00000800;
    internal const int MF_GRAYED = 0x00000001;
    internal const int SM_CXSMICON = 49;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        /// <summary>
        /// Fixed 128 characters, which is where <see cref="TrayPresenter.TooltipLimit"/>
        /// comes from: the shell copies this buffer wholesale, so a longer tooltip is
        /// not truncated, it is rejected.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    internal delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Declared with <c>DllImport</c> rather than <c>LibraryImport</c>: the source
    /// generator cannot marshal the fixed-length string buffers in
    /// <see cref="NOTIFYICONDATA"/>, which are not optional — the shell's struct is
    /// defined that way.
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMessage(out MSG message, IntPtr hWnd, int filterMin, int filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessage(ref MSG message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr menu, int flags, IntPtr itemId, string? item);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int TrackPopupMenuEx(
        IntPtr menu,
        int flags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr parameters);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Marks the item the shell should draw in bold — the Windows convention for "this
    /// is what double-clicking does". Without it that shortcut is undiscoverable.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    internal const int WS_OVERLAPPED = 0x00000000;
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_SYSMENU = 0x00080000;
    internal const int WS_MINIMIZEBOX = 0x00020000;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_TABSTOP = 0x00010000;
    internal const int WS_GROUP = 0x00020000;
    internal const int WS_VSCROLL = 0x00200000;
    internal const int WS_BORDER = 0x00800000;
    internal const int WS_EX_APPWINDOW = 0x00040000;

    internal const int BS_DEFPUSHBUTTON = 0x00000001;
    internal const int BS_AUTOCHECKBOX = 0x00000003;
    internal const int SS_LEFT = 0x00000000;
    internal const int SS_ENDELLIPSIS = 0x00004000;

    /// <summary>
    /// The list is a read-out, not a chooser: nothing in the window acts on a selected
    /// session, and a highlight that leads nowhere invites clicking.
    /// </summary>
    internal const int LBS_NOSEL = 0x00004000;

    /// <summary>
    /// Keeps the box the height we asked for. By default a list box silently shrinks
    /// to a whole number of rows, which would leave it out of line with the buttons.
    /// </summary>
    internal const int LBS_NOINTEGRALHEIGHT = 0x00000100;

    internal const int LB_ADDSTRING = 0x0180;
    internal const int LB_RESETCONTENT = 0x0184;
    internal const int LB_SETTOPINDEX = 0x0197;
    internal const int LB_GETTOPINDEX = 0x018E;

    internal const int BM_GETCHECK = 0x00F0;
    internal const int BM_SETCHECK = 0x00F1;
    internal const int BST_UNCHECKED = 0;
    internal const int BST_CHECKED = 1;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    /// <summary>
    /// How a window is shown for the first time, when <c>ShowWindow</c> cannot be
    /// trusted to do it.
    /// <para>
    /// The first <c>ShowWindow</c> call a process makes ignores the command it is
    /// given and uses whatever the launcher put in <c>STARTUPINFO</c> instead. The
    /// agent is launched deliberately hidden — that is the whole point of the
    /// scheduled task's <c>Hidden</c> setting — so that inherited command is
    /// <c>SW_HIDE</c>, and the first window the agent ever opens would be created,
    /// laid out, and then quietly hidden. <c>SetWindowPos</c> has no such rule.
    /// </para>
    /// </summary>
    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOZORDER = 0x0004;
    internal const int SWP_SHOWWINDOW = 0x0040;

    internal const int COLOR_BTNFACE = 15;
    internal const int TRANSPARENT = 1;

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    /// <summary>The two commands <c>IsDialogMessage</c> synthesises, for Enter and Escape.</summary>
    internal const int IDOK = 1;
    internal const int IDCANCEL = 2;

    internal const int MB_OK = 0x00000000;
    internal const int MB_ICONERROR = 0x00000010;
    internal const int MB_ICONWARNING = 0x00000030;
    internal const int MB_ICONINFORMATION = 0x00000040;

    internal const int DEFAULT_CHARSET = 1;
    internal const int FW_NORMAL = 400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The same call, for the messages whose <c>lParam</c> is text. A separate
    /// declaration rather than a cast, so the marshaller allocates and frees the
    /// native string for us.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageString(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowText(IntPtr hWnd, string text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// What makes Tab move between the controls and Escape close the window. A window
    /// created with <c>CreateWindowEx</c> gets neither for free; both come from
    /// feeding its messages through here before dispatching them.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsDialogMessage(IntPtr dialog, ref MSG message);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetTimer(IntPtr hWnd, IntPtr eventId, uint milliseconds, IntPtr callback);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(IntPtr hWnd, IntPtr eventId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRect(ref RECT rect, int style, [MarshalAs(UnmanagedType.Bool)] bool hasMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(
        IntPtr hWnd,
        int x,
        int y,
        int width,
        int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    /// <summary>
    /// Present since Windows 10 1607, which is below our floor, so no probing for it.
    /// Returns 96 when the process is not DPI aware, which is the honest answer: the
    /// OS is bitmap-scaling us and the layout should be built at 96.
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetSysColorBrush(int index);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr owner, string text, string caption, int type);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        int italic,
        int underline,
        int strikeOut,
        int charSet,
        int outputPrecision,
        int clipPrecision,
        int quality,
        int pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(IntPtr deviceContext, int mode);
}
