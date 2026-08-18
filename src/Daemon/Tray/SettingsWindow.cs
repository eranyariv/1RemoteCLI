using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>How loudly to say something.</summary>
public enum NoticeKind
{
    Information,
    Warning,
    Problem,
}

/// <summary>Something to tell the user after they asked for an action.</summary>
public readonly record struct SettingsNotice(string Text, NoticeKind Kind);

/// <summary>
/// Everything the settings window is allowed to do, as delegates.
/// <para>
/// The window knows about pixels and message loops and nothing else. Handing it
/// closures rather than the agent, the installer and the shortcut writer keeps the
/// one file in this codebase that cannot be unit-tested down to layout, and lets the
/// parts that can be tested stay where they are.
/// </para>
/// </summary>
/// <param name="Read">The current state, asked for once a second while the window is open.</param>
/// <param name="ReadStartAtLogon">
/// Whether the agent really is set to start at logon. Asked, not remembered — the
/// scheduled task and the Run key can both be changed behind our back.
/// </param>
/// <param name="WriteStartAtLogon">Turns it on or off; returns why not, or null when it worked.</param>
/// <param name="WrapShortcut">
/// Prompts for a shortcut and wraps it, given the window to parent the file dialog on.
/// Returns null when the user cancelled, so nothing is said.
/// </param>
public sealed record SettingsActions(
    Func<SettingsView> Read,
    Func<bool> ReadStartAtLogon,
    Func<bool, string?> WriteStartAtLogon,
    Action SignIn,
    Action SignOut,
    Action OpenLogs,
    Action SendFeedback,
    Func<IntPtr, SettingsNotice?> WrapShortcut);

/// <summary>
/// The agent's settings window.
/// <para>
/// Raw Win32, for the same reason the tray icon is: a dialog is not worth 22 MB of
/// Windows Desktop runtime in every download (issue #46). What that costs is this
/// file — the layout is arithmetic rather than a designer — and the price is paid
/// once, here, rather than by every user on every update.
/// </para>
/// <para>
/// Lives on the tray's thread. A window has to be created on the thread that pumps
/// its messages, and the tray already owns one that is STA and idle.
/// </para>
/// <para>
/// Reads rather than remembers. Everything shown is asked for again on a timer, so
/// the window cannot sit there stating something that stopped being true — which is
/// the specific failure that makes a settings dialog worse than no dialog.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsWindow
{
    private const int IdClose = 100;
    private const int IdSignInOut = 101;
    private const int IdStartAtLogon = 102;
    private const int IdWrapShortcut = 103;
    private const int IdOpenLogs = 104;
    private const int IdSendFeedback = 105;

    private const int RefreshTimer = 1;
    private const uint RefreshMilliseconds = 1000;

    /// <summary>Layout at 96 DPI, scaled up from there. Widths first, then the rows.</summary>
    private const int Margin = 14;
    private const int ClientWidth = 474;
    private const int ClientHeight = 364;
    private const int RowHeight = 18;
    private const int ButtonHeight = 26;

    private readonly SettingsActions _actions;
    private readonly WndProc _wndProc;
    private readonly string _className = $"1RemoteCLI.Settings.{Environment.ProcessId}";

    private bool _classRegistered;
    private int _dpi = 96;

    private IntPtr _window;
    private IntPtr _font;
    private IntPtr _accountLabel;
    private IntPtr _connectionLabel;
    private IntPtr _signInOut;
    private IntPtr _sessions;
    private IntPtr _startAtLogon;
    private IntPtr _versionLabel;

    /// <summary>
    /// What the list currently shows. Kept so the refresh can leave the box alone when
    /// nothing changed: refilling a list box scrolls it back to the top, and doing that
    /// once a second would make a list of more than a few sessions unreadable.
    /// </summary>
    private IReadOnlyList<string> _shown = [];

    internal SettingsWindow(SettingsActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _wndProc = HandleMessage;
    }

    /// <summary>
    /// Opens the window, or brings the open one forward.
    /// <para>
    /// Must be called on the tray thread. Asking twice has to focus what is already
    /// there rather than stack a second copy, because the tray icon is the only way in
    /// and clicking it twice is what people do when the first click seemed to do
    /// nothing.
    /// </para>
    /// </summary>
    internal void Show()
    {
        if (_window != IntPtr.Zero)
        {
            if (IsIconic(_window))
            {
                ShowWindow(_window, SW_RESTORE);
            }

            SetForegroundWindow(_window);
            return;
        }

        try
        {
            Create();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // Same bargain as the tray icon: the agent's job is relaying sessions, and
            // a window that would not open is not worth ending the process over.
            Destroy();
            return;
        }

        Refresh(reread: true);

        // Not ShowWindow. A process's *first* ShowWindow call ignores the command it is
        // given and obeys the launcher's STARTUPINFO instead, and this agent is started
        // deliberately hidden — by the scheduled task, by the installer, by anything
        // that does not want a console flashing up at logon. So the one call that has to
        // work is exactly the one Windows overrules, and the window is created, sized,
        // filled and then hidden, with no error anywhere to say so. SetWindowPos is not
        // subject to the rule.
        SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
        SetForegroundWindow(_window);
    }

    /// <summary>
    /// Gives the window first refusal on a message from the tray's loop.
    /// <para>
    /// This is what makes Tab move between the controls and Escape close the window. A
    /// window built with <c>CreateWindowEx</c> has neither until its messages are put
    /// through <c>IsDialogMessage</c>, and a settings dialog that cannot be tabbed
    /// through is not reachable by anyone using a screen reader.
    /// </para>
    /// </summary>
    internal bool Handles(ref MSG message) =>
        _window != IntPtr.Zero && IsDialogMessage(_window, ref message);

    internal void Close()
    {
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
        }
    }

    /// <summary>
    /// Pulls the application icon out of this executable at the size Windows asks for.
    /// <para>
    /// The binary already carries every frame from 16 up to 256, so there is nothing to
    /// ship alongside it and nothing to scale by hand — passing the wanted size lets the
    /// loader pick the frame drawn for it rather than resampling the largest one.
    /// </para>
    /// <para>
    /// A missing or unreadable icon is not worth failing the dialog over. Returning
    /// <see cref="IntPtr.Zero"/> puts us back exactly where we were before, with Windows
    /// falling back to the stock icon, which is a cosmetic loss and nothing more.
    /// </para>
    /// </summary>
    private static IntPtr AppIcon(IntPtr instance, int widthMetric, int heightMetric)
    {
        return LoadImage(
            instance,
            (IntPtr)ApplicationIconResourceId,
            IMAGE_ICON,
            GetSystemMetrics(widthMetric),
            GetSystemMetrics(heightMetric),

            // Shared, so the system owns the handle. The class outlives the process's
            // interest in it and is never unregistered, so there is nothing to free.
            LR_SHARED);
    }

    private void Create()
    {
        IntPtr instance = GetModuleHandle(null);

        if (!_classRegistered)
        {
            var windowClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className,

                // The dialog grey every other Windows dialog uses. Owned by the system,
                // so it is never deleted.
                hbrBackground = GetSysColorBrush(COLOR_BTNFACE),

                // Both sizes, because Windows uses them for different things: the small
                // one is the caption bar and the Alt+Tab thumbnail, the large one is the
                // task switcher. Setting only one leaves the shell to stretch the other.
                hIcon = AppIcon(instance, SM_CXICON, SM_CYICON),
                hIconSm = AppIcon(instance, SM_CXSMICON, SM_CYSMICON),
            };

            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"Registering the settings window class failed: {Marshal.GetLastWin32Error()}");
            }

            _classRegistered = true;
        }

        // No resize border and no maximise box: the layout is fixed arithmetic, so a
        // window the user can stretch would only ever show more grey.
        const int Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;

        _window = CreateWindowEx(
            WS_EX_APPWINDOW,
            _className,
            SettingsPresenter.Title,
            Style,
            0,
            0,
            100,
            100,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Creating the settings window failed: {Marshal.GetLastWin32Error()}");
        }

        // Deliberately thrown away. Windows overrules the first ShowWindow call a
        // process makes, substituting whatever the launcher asked for in STARTUPINFO —
        // which for an agent started by the scheduled task is "hidden". Spending that
        // one call here, on a window that is not on screen yet and is about to be shown
        // by SetWindowPos anyway, means every later call behaves as written. Without it
        // the minimise box is a trap: restoring the window would be the first call
        // instead, and would silently do nothing.
        ShowWindow(_window, SW_HIDE);

        // Only askable once the window exists, which is why the size is applied after
        // creation rather than passed in.
        uint dpi = GetDpiForWindow(_window);
        _dpi = dpi == 0 ? 96 : (int)dpi;

        _font = CreateFont(
            // Negative means "this many pixels of character height" rather than cell
            // height, which is how a point size is requested. 9pt Segoe UI is the shell
            // dialog font; asking the system for NONCLIENTMETRICS would be more correct
            // and costs a 500-byte struct whose layout changes between Windows versions.
            -(9 * _dpi / 72),
            0,
            0,
            0,
            FW_NORMAL,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            0,
            0,
            0,
            0,
            "Segoe UI");

        Size(Style);
        CreateControls(instance);

        SetTimer(_window, RefreshTimer, RefreshMilliseconds, IntPtr.Zero);
    }

    /// <summary>Sizes to the client area we want, then centres on the screen.</summary>
    private void Size(int style)
    {
        var bounds = new RECT
        {
            right = Scale(ClientWidth),
            bottom = Scale(ClientHeight),
        };

        // The caption and borders are not part of what we laid out, and they differ by
        // theme and DPI, so the frame is measured rather than guessed at.
        AdjustWindowRect(ref bounds, style, false);

        int width = bounds.right - bounds.left;
        int height = bounds.bottom - bounds.top;

        MoveWindow(
            _window,
            Math.Max(0, (GetSystemMetrics(SM_CXSCREEN) - width) / 2),
            Math.Max(0, (GetSystemMetrics(SM_CYSCREEN) - height) / 2),
            width,
            height,
            false);
    }

    private void CreateControls(IntPtr instance)
    {
        int content = ClientWidth - (Margin * 2);
        const int buttonWidth = 104;
        int y = Margin;

        // Row one: who, and the button that changes it. The button is on the same line
        // because signing in or out is the one thing this line ever leads to.
        _accountLabel = Static(instance, Margin, y + 4, content - buttonWidth - 10, RowHeight);
        _signInOut = Button(instance, IdSignInOut, ClientWidth - Margin - buttonWidth, y, buttonWidth, ButtonHeight);
        y += ButtonHeight + 6;

        // Row two: whether the phone can see this machine. Directly under the account
        // because signed out is one of the reasons it cannot. Two rows tall and without
        // the ellipsis style, so the longest of these sentences wraps rather than losing
        // its ending — which in one case is the part that says sessions still work.
        _connectionLabel = Static(instance, Margin, y, content, RowHeight * 2, style: SS_LEFT);
        y += (RowHeight * 2) + 12;

        Static(instance, Margin, y, content, RowHeight, SettingsPresenter.SessionsLabel);
        y += RowHeight + 4;

        _sessions = Create(
            instance,
            "LISTBOX",
            WS_CHILD | WS_VISIBLE | WS_BORDER | WS_VSCROLL | WS_TABSTOP | LBS_NOSEL | LBS_NOINTEGRALHEIGHT,
            Margin,
            y,
            content,
            116,
            IntPtr.Zero);
        y += 116 + 14;

        _startAtLogon = Create(
            instance,
            "BUTTON",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
            Margin,
            y,
            content,
            22,
            IdStartAtLogon,
            SettingsPresenter.StartAtLogonLabel);
        y += 22 + 14;

        // The one row of actions. Wrapping a shortcut lives here rather than in the
        // tray menu (issue #66): it is a rare, deliberate act, and the tray menu is for
        // the things that are needed in a hurry.
        const int wrapWidth = 176;        Button(instance, IdWrapShortcut, Margin, y, wrapWidth, ButtonHeight, SettingsPresenter.WrapShortcutLabel);
        Button(instance, IdOpenLogs, Margin + wrapWidth + 8, y, 96, ButtonHeight, SettingsPresenter.OpenLogsLabel);
        Button(
            instance,
            IdSendFeedback,
            Margin + wrapWidth + 8 + 96 + 8,
            y,
            ClientWidth - Margin - (Margin + wrapWidth + 8 + 96 + 8),
            ButtonHeight,
            SettingsPresenter.SendFeedbackLabel);
        y += ButtonHeight + 16;

        // The version is the first thing any bug report needs, so it sits next to the
        // button that sends one.
        _versionLabel = Static(instance, Margin, y + 5, 200, RowHeight);
        Button(
            instance,
            IdClose,
            ClientWidth - Margin - 92,
            y,
            92,
            ButtonHeight,
            SettingsPresenter.CloseLabel,
            BS_DEFPUSHBUTTON);
    }

    private IntPtr Static(
        IntPtr instance,
        int x,
        int y,
        int width,
        int height,
        string text = "",
        int style = SS_LEFT | SS_ENDELLIPSIS) =>
        Create(
            instance,
            "STATIC",
            WS_CHILD | WS_VISIBLE | style,
            x,
            y,
            width,
            height,
            IntPtr.Zero,
            text);

    private IntPtr Button(
        IntPtr instance,
        int id,
        int x,
        int y,
        int width,
        int height,
        string text = "",
        int extraStyle = 0) =>
        Create(
            instance,
            "BUTTON",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_GROUP | extraStyle,
            x,
            y,
            width,
            height,
            id,
            text);

    private IntPtr Create(
        IntPtr instance,
        string className,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr id,
        string text = "")
    {
        IntPtr control = CreateWindowEx(
            0,
            className,
            text,
            style,
            Scale(x),
            Scale(y),
            Scale(width),
            Scale(height),
            _window,
            id,
            instance,
            IntPtr.Zero);

        if (control != IntPtr.Zero && _font != IntPtr.Zero)
        {
            // Without this every control draws in the 1990s bitmap system font, which
            // is the single most obvious way a hand-built window looks broken.
            SendMessage(control, WM_SETFONT, _font, 1);
        }

        return control;
    }

    private int Scale(int value) => value * _dpi / 96;

    /// <summary>
    /// Pulls the current state in and puts it on screen.
    /// </summary>
    /// <param name="reread">
    /// Whether to ask about start-at-logon too. Only on open and after a toggle: that
    /// answer comes from running <c>schtasks.exe</c>, and doing that once a second
    /// would spawn a process a second for as long as the window is open.
    /// </param>
    private void Refresh(bool reread)
    {
        SettingsView view = _actions.Read();

        SetWindowText(_accountLabel, view.Account);
        SetWindowText(_connectionLabel, view.Connection);
        SetWindowText(_versionLabel, view.Version);
        SetWindowText(
            _signInOut,
            view.SignedIn ? SettingsPresenter.SignOutLabel : SettingsPresenter.SignInLabel);

        if (!_shown.SequenceEqual(view.Sessions))
        {
            FillSessions(view.Sessions);
        }

        if (reread)
        {
            Check(_startAtLogon, Ask(_actions.ReadStartAtLogon));
        }
    }

    private void FillSessions(IReadOnlyList<string> lines)
    {
        // Preserved across the rebuild, so a session ending three rows up does not yank
        // the list out from under someone reading the bottom of it.
        IntPtr top = SendMessage(_sessions, LB_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);

        SendMessage(_sessions, LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);

        foreach (string line in lines)
        {
            SendMessageString(_sessions, LB_ADDSTRING, IntPtr.Zero, line);
        }

        SendMessage(_sessions, LB_SETTOPINDEX, top, IntPtr.Zero);

        _shown = lines;
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_COMMAND:
                OnCommand((int)((long)wParam & 0xFFFF));
                return IntPtr.Zero;

            case WM_TIMER:
                Refresh(reread: false);
                return IntPtr.Zero;

            case WM_CTLCOLORSTATIC:
                // Labels and the checkbox paint their own background otherwise, leaving
                // white rectangles on the dialog grey.
                SetBkMode(wParam, TRANSPARENT);
                return GetSysColorBrush(COLOR_BTNFACE);

            case WM_CLOSE:
                DestroyWindow(window);
                return IntPtr.Zero;

            case WM_DESTROY:
                Destroy();
                return IntPtr.Zero;

            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            // Enter and Escape, synthesised by IsDialogMessage. Both close: there is
            // nothing here to confirm, every change has already been applied.
            case IDOK:
            case IDCANCEL:
            case IdClose:
                Close();
                break;

            case IdSignInOut:
                OffThread(_actions.Read().SignedIn ? _actions.SignOut : _actions.SignIn);
                break;

            case IdOpenLogs:
                OffThread(_actions.OpenLogs);
                break;

            case IdSendFeedback:
                OffThread(_actions.SendFeedback);
                break;

            case IdStartAtLogon:
                OnStartAtLogonToggled();
                break;

            case IdWrapShortcut:
                OnWrapShortcut();
                break;
        }
    }

    /// <summary>
    /// Applies the checkbox, then puts back whatever is actually true.
    /// <para>
    /// The control has already toggled itself by the time we hear about it, so a
    /// refusal — no Task Scheduler, no permission to write the Run key — would
    /// otherwise leave a tick sitting next to something that is not happening.
    /// </para>
    /// <para>
    /// Done on this thread, and it does briefly block: it shells out to
    /// <c>schtasks.exe</c>. Doing it in the background would mean the box could be
    /// clicked again while the first change was still in flight, and two of these
    /// racing is how the task and the Run key end up disagreeing.
    /// </para>
    /// </summary>
    private void OnStartAtLogonToggled()
    {
        bool wanted = SendMessage(_startAtLogon, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == BST_CHECKED;

        string? problem;

        try
        {
            problem = _actions.WriteStartAtLogon(wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = ex.Message;
        }

        Check(_startAtLogon, Ask(_actions.ReadStartAtLogon));

        if (problem is not null)
        {
            Say(new SettingsNotice(problem, NoticeKind.Problem));
        }
    }

    private void OnWrapShortcut()
    {
        SettingsNotice? notice;

        try
        {
            // Inline, not on a worker: this opens a shell file dialog, which pumps its
            // own messages and has to be owned by this window to be modal to it.
            notice = _actions.WrapShortcut(_window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException)
        {
            notice = new SettingsNotice(ex.Message, NoticeKind.Problem);
        }

        if (notice is { } result)
        {
            Say(result);
        }
    }

    private void Say(SettingsNotice notice)
    {
        int icon = notice.Kind switch
        {
            NoticeKind.Problem => MB_ICONERROR,
            NoticeKind.Warning => MB_ICONWARNING,
            _ => MB_ICONINFORMATION,
        };

        MessageBox(_window, notice.Text, SettingsPresenter.Title, MB_OK | icon);
    }

    /// <summary>
    /// Runs the actions that start other programs off the message loop, so a slow
    /// browser or mail client cannot freeze the window that launched it.
    /// </summary>
    private static void OffThread(Action action) => _ = Task.Run(action);

    /// <summary>
    /// Asks a question that talks to the outside world, treating a failure as "no".
    /// The window is a read-out; it must not take the agent down because Task Scheduler
    /// was unavailable for a moment.
    /// </summary>
    private static bool Ask(Func<bool> question)
    {
        try
        {
            return question();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Check(IntPtr control, bool state) =>
        SendMessage(control, BM_SETCHECK, state ? BST_CHECKED : BST_UNCHECKED, IntPtr.Zero);

    private void Destroy()
    {
        if (_window != IntPtr.Zero)
        {
            KillTimer(_window, RefreshTimer);
        }

        if (_font != IntPtr.Zero)
        {
            DeleteObject(_font);
            _font = IntPtr.Zero;
        }

        _window = IntPtr.Zero;
        _accountLabel = IntPtr.Zero;
        _connectionLabel = IntPtr.Zero;
        _signInOut = IntPtr.Zero;
        _sessions = IntPtr.Zero;
        _startAtLogon = IntPtr.Zero;
        _versionLabel = IntPtr.Zero;
        _shown = [];
    }
}
