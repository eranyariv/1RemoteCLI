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
    internal const int WM_SIZE = 0x0005;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_GETMINMAXINFO = 0x0024;
    internal const int WM_NOTIFY = 0x004E;
    internal const int WM_ERASEBKGND = 0x0014;
    internal const int WM_PAINT = 0x000F;
    internal const int WM_SETFONT = 0x0030;
    internal const int SIZE_RESTORED = 0;
    internal const int SPI_GETWORKAREA = 0x0030;

    /// <summary>
    /// Broadcast when a system-wide setting changes. The one that matters here arrives
    /// with "ImmersiveColorSet" in <c>lParam</c>, and is how a window hears that the
    /// user moved the light/dark switch while it was open.
    /// </summary>
    internal const int WM_SETTINGCHANGE = 0x001A;

    internal const int WM_THEMECHANGED = 0x031A;
    internal const int WM_COMMAND = 0x0111;
    internal const int WM_TIMER = 0x0113;
    internal const int WM_CTLCOLORSTATIC = 0x0138;
    /// <summary>
    /// Sent when the window is dragged onto a monitor with a different scale factor.
    /// Only ever received by a per-monitor-v2 process, which this became in
    /// <c>app.manifest</c>; ignoring it there would leave the window at the old size,
    /// which is worse than the blur it replaced.
    /// </summary>
    internal const int WM_DPICHANGED = 0x02E0;

    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_CONTEXTMENU = 0x007B;
    internal const int WM_USER = 0x0400;

    /// <summary>
    /// Version 4 notification-area activation messages. The shell sends
    /// <see cref="NIN_SELECT"/> for a single left click and
    /// <see cref="NIN_KEYSELECT"/> for keyboard activation instead of raw mouse
    /// messages.
    /// </summary>
    internal const int NIN_SELECT = WM_USER;
    internal const int NIN_KEYSELECT = WM_USER + 1;
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

    internal const int NM_CLICK = -2;
    internal const int NM_RETURN = -4;
    internal const int LVN_COLUMNCLICK = -108;
    internal const int ICC_LINK_CLASS = 0x00008000;
    internal const int ICC_LISTVIEW_CLASSES = 0x00000001;

    internal const int LVM_FIRST = 0x1000;
    internal const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    internal const int LVM_INSERTCOLUMNW = LVM_FIRST + 97;
    internal const int LVM_INSERTITEMW = LVM_FIRST + 77;
    internal const int LVM_SETITEMW = LVM_FIRST + 76;
    internal const int LVM_DELETEALLITEMS = LVM_FIRST + 9;
    internal const int LVM_ENSUREVISIBLE = LVM_FIRST + 19;
    internal const int LVM_GETCOLUMNWIDTH = LVM_FIRST + 29;
    internal const int LVM_SETCOLUMNWIDTH = LVM_FIRST + 30;
    internal const int LVM_GETHEADER = LVM_FIRST + 31;
    internal const int LVM_GETTOPINDEX = LVM_FIRST + 39;

    internal const int LVS_EX_FULLROWSELECT = 0x00000020;
    internal const int LVS_EX_LABELTIP = 0x00004000;
    internal const int LVS_EX_DOUBLEBUFFER = 0x00010000;

    internal const int LVCF_FMT = 0x0001;
    internal const int LVCF_WIDTH = 0x0002;
    internal const int LVCF_TEXT = 0x0004;
    internal const int LVCF_SUBITEM = 0x0008;
    internal const int LVCFMT_LEFT = 0x0000;
    internal const int LVIF_TEXT = 0x0001;

    internal const int HDM_FIRST = 0x1200;
    internal const int HDM_GETITEMW = HDM_FIRST + 11;
    internal const int HDM_SETITEMW = HDM_FIRST + 12;
    internal const int HDI_FORMAT = 0x0004;
    internal const int HDF_SORTDOWN = 0x0200;
    internal const int HDF_SORTUP = 0x0400;

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct NMHDR
    {
        public IntPtr hwndFrom;
        public IntPtr idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NM_LISTVIEW
    {
        public NMHDR hdr;
        public int iItem;
        public int iSubItem;
        public int uNewState;
        public int uOldState;
        public int uChanged;
        public POINT ptAction;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INITCOMMONCONTROLSEX
    {
        public int dwSize;
        public int dwICC;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LVCOLUMN
    {
        public int mask;
        public int fmt;
        public int cx;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszText;

        public int cchTextMax;
        public int iSubItem;
        public int iImage;
        public int iOrder;
        public int cxMin;
        public int cxDefault;
        public int cxIdeal;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LVITEM
    {
        public int mask;
        public int iItem;
        public int iSubItem;
        public int state;
        public int stateMask;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszText;

        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public int cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct HDITEM
    {
        public int mask;
        public int cxy;
        public IntPtr pszText;
        public IntPtr hbm;
        public int cchTextMax;
        public int fmt;
        public IntPtr lParam;
        public int iImage;
        public int iOrder;
        public int type;
        public IntPtr pvFilter;
        public int state;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
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

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX controls);

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

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(
        int action,
        int parameter,
        ref RECT data,
        int update);

    /// <summary>
    /// Loads an image out of a module's resources, at a given size.
    /// <para>
    /// Used to pull the application icon out of this executable rather than shipping a
    /// second copy of it: the .NET SDK stamps <c>ApplicationIcon</c> into the binary as
    /// a group icon, so the frames are already there.
    /// </para>
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImage(
        IntPtr instance,
        IntPtr name,
        uint type,
        int cx,
        int cy,
        uint load);

    internal const uint IMAGE_ICON = 1;

    /// <summary>
    /// Hands back a cached handle the system owns, so the caller must not destroy it.
    /// Right for an icon that lives as long as the process does.
    /// </summary>
    internal const uint LR_SHARED = 0x00008000;

    /// <summary>
    /// The resource id the .NET SDK gives the icon named by <c>ApplicationIcon</c>.
    /// <para>
    /// It collides numerically with <c>IDI_APPLICATION</c>, which is deliberate on the
    /// SDK's part and harmless here: passing this module's handle rather than
    /// <see cref="IntPtr.Zero"/> is what decides whether Windows returns our icon or the
    /// stock one, and a window class left without an icon gets the stock one anyway.
    /// </para>
    /// </summary>
    internal const int ApplicationIconResourceId = 32512;

    internal const int SM_CXICON = 11;
    internal const int SM_CYICON = 12;
    internal const int SM_CYSMICON = 50;

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
    internal const int WS_MAXIMIZEBOX = 0x00010000;
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_CLIPCHILDREN = 0x02000000;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_TABSTOP = 0x00010000;
    internal const int WS_GROUP = 0x00020000;
    internal const int WS_VSCROLL = 0x00200000;
    internal const int WS_BORDER = 0x00800000;
    internal const int WS_EX_APPWINDOW = 0x00040000;

    internal const int BS_DEFPUSHBUTTON = 0x00000001;
    internal const int BS_AUTOCHECKBOX = 0x00000003;
    internal const int BS_AUTORADIOBUTTON = 0x00000009;
    internal const int BS_PUSHLIKE = 0x00001000;
    internal const int SS_LEFT = 0x00000000;
    internal const int SS_CENTER = 0x00000001;
    internal const int SS_ENDELLIPSIS = 0x00004000;

    internal const int LVS_REPORT = 0x0001;
    internal const int LVS_SHOWSELALWAYS = 0x0008;

    internal const int BM_GETCHECK = 0x00F0;
    internal const int BM_SETCHECK = 0x00F1;
    internal const int BST_UNCHECKED = 0;
    internal const int BST_CHECKED = 1;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;
    internal const int SIZE_MINIMIZED = 1;

    /// <summary>
    /// How a window is shown for the first time, when <c>ShowWindow</c> cannot be
    /// trusted to do it.
    /// <para>
    /// The first <c>ShowWindow</c> call a process makes ignores the command it is
    /// given and uses whatever the launcher put in <c>STARTUPINFO</c> instead. The
    /// agent does not choose its launcher — a scheduled task at logon, Explorer, a
    /// shell — so it cannot know what that will be, and a window it opens could be
    /// created, laid out, and then quietly hidden. <c>SetWindowPos</c> has no such
    /// rule.
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

    /// <summary>
    /// The weight Fluent calls "Body Strong". Windows 11 uses it for the line that says
    /// what a group of controls is, and it is the only emphasis in this window.
    /// </summary>
    internal const int FW_SEMIBOLD = 600;

    /// <summary>
    /// Asks GDI for the same subpixel antialiasing the shell uses. The default quality
    /// is whatever the font suggests, which for text this small is visibly rougher than
    /// every other window on the desktop.
    /// </summary>
    internal const int CLEARTYPE_QUALITY = 5;

    /// <summary>
    /// Dark mode for the caption bar. The attribute number changed once, at Windows 10
    /// 20H1; only the later one is used because the agent's floor is build 19041.
    /// </summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>Windows 11's rounded corners, as opposed to letting DWM decide.</summary>
    internal const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fErase;

        public RECT rcPaint;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fRestore;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fIncUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// What <c>comctl32!DllGetVersion</c> fills in. Version 6 only ever arrives through
    /// a side-by-side binding, so the number reported here is really an answer about the
    /// application manifest rather than about the library.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DLLVERSIONINFO
    {
        public uint cbSize;

        public uint dwMajorVersion;

        public uint dwMinorVersion;

        public uint dwBuildNumber;

        public uint dwPlatformID;
    }

    /// <summary>
    /// Resolved through the activation context like any other import of it, so this
    /// reports the comctl32 the process actually got: 6.x when the manifest is present,
    /// 5.82 when it is not.
    /// </summary>
    [DllImport("comctl32.dll", EntryPoint = "DllGetVersion")]
    internal static extern int ComCtlGetVersion(ref DLLVERSIONINFO version);

    /// <summary>
    /// <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>. A pseudo-handle rather than a
    /// pointer, which is why it is a negative constant.
    /// </summary>
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr window);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibrary(string fileName);

    /// <summary>
    /// The ordinal form. uxtheme's dark-mode entry points have no names, so there is no
    /// string overload that would reach them.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    internal static IntPtr GetProcAddress(IntPtr module, int ordinal) =>
        GetProcAddress(module, (IntPtr)ordinal);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(IntPtr deviceContext, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetBkColor(IntPtr deviceContext, uint color);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(IntPtr deviceContext, ref RECT rect, IntPtr brush);

    [LibraryImport("user32.dll")]
    internal static partial int FrameRect(IntPtr deviceContext, ref RECT rect, IntPtr brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(IntPtr window, ref PAINTSTRUCT paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    /// <summary>
    /// Repaints a window and everything inside it. Needed when the theme changes: the
    /// children have each been re-themed, and each of them is still showing what it drew
    /// under the old one.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, int flags);

    internal const int RDW_INVALIDATE = 0x0001;
    internal const int RDW_ERASE = 0x0004;
    internal const int RDW_ALLCHILDREN = 0x0080;
    internal const int RDW_UPDATENOW = 0x0100;
    internal const int RDW_FRAME = 0x0400;

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

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageListColumn(IntPtr hWnd, int msg, IntPtr wParam, ref LVCOLUMN lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageListItem(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageHeaderItem(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowText(IntPtr hWnd, string text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(IntPtr hWnd);

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
