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
/// <param name="Update">
/// Installs the release the agent has found. Only ever invoked when
/// <see cref="SettingsView.CanUpdate"/> was true, and it says nothing back: what
/// happened arrives through <see cref="Read"/> like everything else in this window,
/// because the update outlives the click by a minute or two.
/// </param>
/// <param name="CheckForUpdate">
/// Asks for the periodic check to happen now, both when the window opens and when the
/// user presses the always-available check button.
/// </param>
public sealed record SettingsActions(
    Func<SettingsView> Read,
    Func<bool> ReadStartAtLogon,
    Func<bool, string?> WriteStartAtLogon,
    Action SignIn,
    Action SignOut,
    Action OpenLogs,
    Action SendFeedback,
    Action OpenChangeHistory,
    Func<IntPtr, SettingsNotice?> WrapShortcut,
    Action Update,
    Action? CheckForUpdate = null,
    Func<SettingsWindowLayout>? ReadLayout = null,
    Action<SettingsWindowLayout>? WriteLayout = null);

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
    private const int IdUpdate = 106;
    private const int IdCheckForUpdates = 107;
    private const int IdTabs = 108;

    private const int Style =
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX |
        WS_MAXIMIZEBOX | WS_THICKFRAME | WS_CLIPCHILDREN;

    private const int RefreshTimer = 1;
    private const int PersistTimer = 2;
    private const uint RefreshMilliseconds = 1000;
    private const uint PersistMilliseconds = 500;

    /// <summary>
    /// Layout at 96 DPI, scaled from there, on Fluent's four-pixel grid.
    /// <para>
    /// The numbers are the Windows 11 ones rather than the Win32 ones they replaced:
    /// a 20px window margin instead of 14, 32px-high buttons instead of 26, and 20px
    /// between groups against 8 inside one. What made the old window read as old was
    /// not the controls, it was that everything was 6 or 8 pixels from everything else,
    /// so nothing looked grouped with anything.
    /// </para>
    /// </summary>
    private const int Margin = 20;

    /// <summary>Between two groups. Inside one, things sit <see cref="Tight"/> apart.</summary>
    private const int Gap = 20;

    private const int Tight = 8;

    /// <summary>One line of body text at 14px, with room for descenders.</summary>
    private const int RowHeight = 20;

    private const int ButtonHeight = 32;

    /// <summary>
    /// Fluent's Body size, in pixels at 96 DPI. The Win32 shell dialog font is 9pt,
    /// which is 12px; Windows 11 sets its own UI two pixels larger, and matching it is
    /// most of what makes a window look current.
    /// </summary>
    private const int BodySize = 14;

    /// <summary>Fluent's Caption size, for the version line.</summary>
    private const int CaptionSize = 12;

    private readonly SettingsActions _actions;
    private readonly WndProc _wndProc;
    private readonly string _className = $"1RemoteCLI.Settings.{Environment.ProcessId}";

    /// <summary>
    /// Every child, so fonts and themes can be reapplied as one set.
    /// <para>
    /// The geometry is recomputed from the current client rectangle; only membership
    /// lives here.
    /// </para>
    /// </summary>
    private readonly List<IntPtr> _controls = [];
    private readonly List<IntPtr> _statusControls = [];
    private readonly List<IntPtr> _sessionControls = [];
    private readonly List<IntPtr> _settingsControls = [];

    private bool _classRegistered;
    private int _dpi = 96;

    private IntPtr _window;
    private IntPtr _font;
    private IntPtr _strongFont;
    private IntPtr _captionFont;
    private Theme _theme = Theme.Current();
    private IntPtr _tabs;
    private IntPtr _accountOrb;
    private IntPtr _accountLabel;
    private IntPtr _connectionOrb;
    private IntPtr _connectionLabel;
    private IntPtr _sessionsLabel;
    private IntPtr _signInOut;
    private IntPtr _sessions;
    private IntPtr _sessionsEmpty;
    private IntPtr _wrapShortcut;
    private IntPtr _startAtLogon;
    private IntPtr _versionLabel;
    private IntPtr _changeHistory;
    private IntPtr _updateLabel;
    private IntPtr _update;
    private IntPtr _checkForUpdates;
    private IntPtr _openLogs;
    private IntPtr _sendFeedback;
    private IntPtr _close;
    private StatusTone _accountTone = StatusTone.Disabled;
    private StatusTone _connectionTone = StatusTone.Disabled;
    private SettingsPage _page;
    private SettingsWindowLayout _layout = SettingsWindowLayout.Default;
    private SessionTableColumn _sortColumn = SessionTableColumn.Activity;
    private bool _sortDescending = true;

    /// <summary>
    /// What the table currently shows. Kept so the refresh can leave it alone when
    /// nothing changed: refilling it scrolls back to the top.
    /// </summary>
    private IReadOnlyList<SessionRow> _shown = [];

    private enum SettingsPage
    {
        Status,
        Sessions,
        Settings,
    }

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
        // Before anything is drawn, and on every open rather than only the first: the
        // answer arrives through the same once-a-second refresh as everything else.
        _actions.CheckForUpdate?.Invoke();

        if (_window != IntPtr.Zero)
        {
            if (IsIconic(_window))
            {
                ShowWindow(_window, SW_RESTORE);
            }

            SetForegroundWindow(_window);
            return;
        }

        _layout = (_actions.ReadLayout?.Invoke() ?? SettingsWindowLayout.Default).Normalize();
        _sortColumn = _layout.SortColumn;
        _sortDescending = _layout.SortDescending;

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
            PersistLayout();
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

                // No class brush. The background is painted in WM_ERASEBKGND from the
                // current theme's surface colour, because the class brush is fixed at
                // registration and the theme is not: the user can move the light/dark
                // switch while this window is open.
                hbrBackground = IntPtr.Zero,

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

        // Before anything is drawn: the caption bar cannot be repainted by us, and a
        // window that flashes white and then goes dark is worse than one that was never
        // themed at all.
        _theme.ApplyToWindow(_window);

        CreateFonts();
        CreateControls(instance);
        Size(Style);

        SetTimer(_window, RefreshTimer, RefreshMilliseconds, IntPtr.Zero);
    }

    /// <summary>
    /// The three weights this window is set in: Body, Body Strong for the two lines
    /// that name things, and Caption for the version.
    /// </summary>
    private void CreateFonts()
    {
        DeleteFonts();

        _font = Font(BodySize, FW_NORMAL);
        _strongFont = Font(BodySize, FW_SEMIBOLD);
        _captionFont = Font(CaptionSize, FW_NORMAL);
    }

    private IntPtr Font(int size, int weight) =>
        CreateFont(
            // Negative means "this many pixels of character height" rather than cell
            // height. The size is already in pixels at 96 DPI, so it scales with the
            // rest of the layout rather than through a point conversion.
            -Scale(size),
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            0,
            0,
            CLEARTYPE_QUALITY,
            0,
            Theme.BodyFace);


    /// <summary>Sizes to the client area we want, then centres on the screen.</summary>
    private void Size(int style)
    {
        var bounds = new RECT
        {
            right = Scale(_layout.ClientWidth),
            bottom = Scale(_layout.ClientHeight),
        };

        // The caption and borders are not part of what we laid out, and they differ by
        // theme and DPI, so the frame is measured rather than guessed at.
        AdjustWindowRect(ref bounds, style, false);

        int width = bounds.right - bounds.left;
        int height = bounds.bottom - bounds.top;

        var workArea = new RECT
        {
            right = GetSystemMetrics(SM_CXSCREEN),
            bottom = GetSystemMetrics(SM_CYSCREEN),
        };
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);

        int availableWidth = workArea.right - workArea.left;
        int availableHeight = workArea.bottom - workArea.top;
        width = Math.Min(width, availableWidth);
        height = Math.Min(height, availableHeight);

        int x = workArea.left + Math.Max(0, (availableWidth - width) / 2);
        int y = workArea.top + Math.Max(0, (availableHeight - height) / 2);

        MoveWindow(
            _window,
            x,
            y,
            width,
            height,
            false);
    }

    private void CreateControls(IntPtr instance)
    {
        var commonControls = new INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC_LINK_CLASS | ICC_TAB_CLASSES | ICC_LISTVIEW_CLASSES,
        };

        if (!InitCommonControlsEx(ref commonControls))
        {
            throw new InvalidOperationException("Windows did not initialize the Settings controls.");
        }

        _tabs = Create(
            instance,
            "SysTabControl32",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            0,
            0,
            100,
            100,
            IdTabs);
        InsertTab(SettingsPage.Status, SettingsPresenter.StatusTabLabel);
        InsertTab(SettingsPage.Sessions, SettingsPresenter.SessionsTabLabel);
        InsertTab(SettingsPage.Settings, SettingsPresenter.SettingsTabLabel);

        _accountOrb = PageControl(
            _statusControls,
            Static(instance, 0, 0, 18, RowHeight, "\u25cf", SS_CENTER));
        _accountLabel = PageControl(
            _statusControls,
            Static(instance, 0, 0, 100, RowHeight, font: _strongFont));
        _signInOut = PageControl(
            _statusControls,
            Button(instance, IdSignInOut, 0, 0, 112, ButtonHeight));
        _connectionOrb = PageControl(
            _statusControls,
            Static(instance, 0, 0, 18, RowHeight, "\u25cf", SS_CENTER));
        _connectionLabel = PageControl(
            _statusControls,
            Static(instance, 0, 0, 100, RowHeight * 2, style: SS_LEFT));
        _versionLabel = PageControl(
            _statusControls,
            Static(instance, 0, 0, 96, RowHeight, font: _captionFont));
        _changeHistory = PageControl(
            _statusControls,
            Create(
                instance,
                "SysLink",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                0,
                0,
                112,
                RowHeight,
                IntPtr.Zero,
                $"<a>{SettingsPresenter.ChangeHistoryLabel}</a>",
                _captionFont));
        _checkForUpdates = PageControl(
            _statusControls,
            Button(
                instance,
                IdCheckForUpdates,
                0,
                0,
                140,
                ButtonHeight,
                SettingsPresenter.CheckForUpdatesLabel));
        _updateLabel = PageControl(
            _statusControls,
            Static(instance, 0, 0, 100, RowHeight * 2, style: SS_LEFT));
        _update = PageControl(
            _statusControls,
            Button(
                instance,
                IdUpdate,
                0,
                0,
                112,
                ButtonHeight,
                SettingsPresenter.UpdateLabel));
        _openLogs = PageControl(
            _statusControls,
            Button(instance, IdOpenLogs, 0, 0, 116, ButtonHeight, SettingsPresenter.OpenLogsLabel));
        _sendFeedback = PageControl(
            _statusControls,
            Button(instance, IdSendFeedback, 0, 0, 116, ButtonHeight, SettingsPresenter.SendFeedbackLabel));

        _sessionsLabel = PageControl(
            _sessionControls,
            Static(
                instance,
                0,
                0,
                100,
                RowHeight,
                SettingsPresenter.SessionsLabel,
                font: _strongFont));
        _sessions = PageControl(
            _sessionControls,
            Create(
                instance,
                "SysListView32",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | LVS_REPORT | LVS_SHOWSELALWAYS,
                0,
                0,
                100,
                100,
                IntPtr.Zero));
        SendMessage(
            _sessions,
            LVM_SETEXTENDEDLISTVIEWSTYLE,
            IntPtr.Zero,
            (IntPtr)(LVS_EX_FULLROWSELECT | LVS_EX_LABELTIP | LVS_EX_DOUBLEBUFFER));
        InsertSessionColumns();
        _sessionsEmpty = PageControl(
            _sessionControls,
            Static(instance, 0, 0, 100, RowHeight, SettingsPresenter.NoSessions, SS_CENTER));
        _wrapShortcut = PageControl(
            _sessionControls,
            Button(instance, IdWrapShortcut, 0, 0, 188, ButtonHeight, SettingsPresenter.WrapShortcutLabel));

        _startAtLogon = PageControl(
            _settingsControls,
            Create(
                instance,
                "BUTTON",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0,
                0,
                100,
                RowHeight + 4,
                IdStartAtLogon,
                SettingsPresenter.StartAtLogonLabel));

        _close = Button(
            instance,
            IdClose,
            0,
            0,
            104,
            ButtonHeight,
            SettingsPresenter.CloseLabel,
            BS_DEFPUSHBUTTON);

        _page = (SettingsPage)_layout.ActiveTab;
        SendMessage(_tabs, TCM_SETCURSEL, (IntPtr)_layout.ActiveTab, IntPtr.Zero);
        ShowPage(_page, persist: false);
        UpdateSortIndicator();
        LayoutControls();
    }

    private void InsertTab(SettingsPage page, string text)
    {
        var item = new TCITEM
        {
            mask = TCIF_TEXT,
            pszText = text,
            cchTextMax = text.Length,
        };
        SendMessageTabItem(_tabs, TCM_INSERTITEMW, (IntPtr)(int)page, ref item);
    }

    private void InsertSessionColumns()
    {
        string[] headings = ["Name", "Source", "Folder", "Status", "Activity"];

        for (int index = 0; index < headings.Length; index++)
        {
            var column = new LVCOLUMN
            {
                mask = LVCF_FMT | LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM,
                fmt = LVCFMT_LEFT,
                cx = Scale(_layout.ColumnWidths[index]),
                pszText = headings[index],
                cchTextMax = headings[index].Length,
                iSubItem = index,
            };
            SendMessageListColumn(_sessions, LVM_INSERTCOLUMNW, (IntPtr)index, ref column);
        }
    }

    private static IntPtr PageControl(List<IntPtr> page, IntPtr control)
    {
        if (control != IntPtr.Zero)
        {
            page.Add(control);
        }

        return control;
    }

    private void ShowPage(SettingsPage page, bool persist = true)
    {
        _page = page;
        ShowPageControls(_statusControls, page == SettingsPage.Status);
        ShowPageControls(_sessionControls, page == SettingsPage.Sessions);
        ShowPageControls(_settingsControls, page == SettingsPage.Settings);
        ShowWindow(
            _sessionsEmpty,
            page == SettingsPage.Sessions && _shown.Count == 0 ? SW_SHOW : SW_HIDE);
        InvalidateRect(_window, IntPtr.Zero, true);

        if (persist)
        {
            SchedulePersist();
        }
    }

    private static void ShowPageControls(IEnumerable<IntPtr> controls, bool show)
    {
        foreach (IntPtr control in controls)
        {
            ShowWindow(control, show ? SW_SHOW : SW_HIDE);
        }
    }

    private void LayoutControls()
    {
        if (_window == IntPtr.Zero || _tabs == IntPtr.Zero ||
            !GetClientRect(_window, out RECT client))
        {
            return;
        }

        int clientWidth = Unscale(client.right);
        int clientHeight = Unscale(client.bottom);
        int content = clientWidth - (Margin * 2);
        int tabHeight = clientHeight - (Margin * 2) - ButtonHeight - 12;
        int pageX = Margin + 16;
        int pageWidth = content - 32;
        int pageY = Margin + 52;
        int pageBottom = Margin + tabHeight - 16;

        Move(_tabs, Margin, Margin, content, tabHeight);
        Move(_close, clientWidth - Margin - 104, clientHeight - Margin - ButtonHeight, 104, ButtonHeight);

        const int orbWidth = 18;
        const int signInWidth = 112;
        int textX = pageX + orbWidth + Tight;
        int textWidth = pageWidth - orbWidth - Tight - signInWidth - Gap;
        int y = pageY;

        Move(_accountOrb, pageX, y + 5, orbWidth, RowHeight);
        Move(_accountLabel, textX, y + 5, textWidth, RowHeight);
        Move(_signInOut, pageX + pageWidth - signInWidth, y, signInWidth, ButtonHeight);

        y += ButtonHeight + Gap;
        Move(_connectionOrb, pageX, y + 2, orbWidth, RowHeight);
        Move(_connectionLabel, textX, y, pageWidth - orbWidth - Tight, RowHeight * 2);

        y += (RowHeight * 2) + Gap;
        const int versionWidth = 96;
        const int historyWidth = 112;
        const int checkWidth = 140;

        Move(_versionLabel, pageX, y + 6, versionWidth, RowHeight);
        Move(_changeHistory, pageX + versionWidth + Tight, y + 6, historyWidth, RowHeight);
        Move(_checkForUpdates, pageX + pageWidth - checkWidth, y, checkWidth, ButtonHeight);

        y += ButtonHeight + Tight;
        const int updateWidth = 112;
        Move(_updateLabel, pageX, y, pageWidth - updateWidth - Gap, RowHeight * 2);
        Move(_update, pageX + pageWidth - updateWidth, y + 4, updateWidth, ButtonHeight);

        y += (RowHeight * 2) + Gap;
        Move(_openLogs, pageX, y, 116, ButtonHeight);
        Move(_sendFeedback, pageX + 116 + Tight, y, 116, ButtonHeight);

        y = pageY;
        Move(_sessionsLabel, pageX, y, pageWidth, RowHeight);
        int tableY = y + RowHeight + Tight;
        int wrapY = pageBottom - ButtonHeight;
        int tableHeight = Math.Max(RowHeight * 3, wrapY - Gap - tableY);
        Move(_sessions, pageX, tableY, pageWidth, tableHeight);
        Move(_sessionsEmpty, pageX + 12, tableY + 40, pageWidth - 24, RowHeight);
        Move(_wrapShortcut, pageX, wrapY, 188, ButtonHeight);

        Move(_startAtLogon, pageX, pageY, pageWidth, RowHeight + 4);
    }

    private void Move(IntPtr control, int x, int y, int width, int height)
    {
        if (control != IntPtr.Zero)
        {
            MoveWindow(control, Scale(x), Scale(y), Scale(width), Scale(height), false);
        }
    }

    private IntPtr Static(
        IntPtr instance,
        int x,
        int y,
        int width,
        int height,
        string text = "",
        int style = SS_LEFT | SS_ENDELLIPSIS,
        IntPtr font = default) =>
        Create(
            instance,
            "STATIC",
            WS_CHILD | WS_VISIBLE | style,
            x,
            y,
            width,
            height,
            IntPtr.Zero,
            text,
            font);

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
        string text = "",
        IntPtr font = default)
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

        if (control == IntPtr.Zero)
        {
            return control;
        }

        _controls.Add(control);

        SetFont(control, font);

        // Buttons, checkboxes and the table draw themselves, so their colours cannot
        // be set with a brush the way a label's can. Handing them the dark Explorer
        // theme is how comctl32 is told to use the artwork it already has.
        _theme.ApplyToControl(control);

        return control;
    }

    private void SetFont(IntPtr control, IntPtr font = default)
    {
        IntPtr wanted = font == default ? _font : font;

        if (control != IntPtr.Zero && wanted != IntPtr.Zero)
        {
            // Without this every control draws in the 1990s bitmap system font, which
            // is the single most obvious way a hand-built window looks broken.
            SendMessage(control, WM_SETFONT, wanted, 1);
        }
    }

    private int Scale(int value) => value * _dpi / 96;

    private int Unscale(int value) => value * 96 / _dpi;

    private void SchedulePersist()
    {
        if (_window != IntPtr.Zero && _actions.WriteLayout is not null)
        {
            SetTimer(_window, PersistTimer, PersistMilliseconds, IntPtr.Zero);
        }
    }

    private void PersistLayout()
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        KillTimer(_window, PersistTimer);

        if (_actions.WriteLayout is null)
        {
            return;
        }

        int clientWidth = _layout.ClientWidth;
        int clientHeight = _layout.ClientHeight;

        if (!IsZoomed(_window) && !IsIconic(_window) && GetClientRect(_window, out RECT client))
        {
            clientWidth = Unscale(client.right);
            clientHeight = Unscale(client.bottom);
        }

        _layout = new SettingsWindowLayout
        {
            ClientWidth = clientWidth,
            ClientHeight = clientHeight,
            ActiveTab = (int)_page,
            ColumnWidths = ReadColumnWidths(),
            SortColumn = _sortColumn,
            SortDescending = _sortDescending,
        }.Normalize();

        _actions.WriteLayout(_layout);
    }

    private void RememberRestoredSize()
    {
        if (GetClientRect(_window, out RECT client))
        {
            _layout = _layout with
            {
                ClientWidth = Unscale(client.right),
                ClientHeight = Unscale(client.bottom),
            };
        }
    }

    private int[] ReadColumnWidths()
    {
        if (_sessions == IntPtr.Zero)
        {
            return [.. _layout.ColumnWidths];
        }

        int[] widths = new int[5];

        for (int index = 0; index < widths.Length; index++)
        {
            int physical = (int)SendMessage(_sessions, LVM_GETCOLUMNWIDTH, (IntPtr)index, IntPtr.Zero);
            widths[index] = Math.Clamp(
                Unscale(physical),
                SettingsWindowLayout.MinimumColumnWidth,
                SettingsWindowLayout.MaximumColumnWidth);
        }

        return widths;
    }

    private void SetColumnWidths(IReadOnlyList<int> widths)
    {
        if (_sessions == IntPtr.Zero)
        {
            return;
        }

        for (int index = 0; index < Math.Min(5, widths.Count); index++)
        {
            SendMessage(_sessions, LVM_SETCOLUMNWIDTH, (IntPtr)index, (IntPtr)Scale(widths[index]));
        }
    }

    private void SetMinimumSize(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return;
        }

        var minimum = new RECT
        {
            right = Scale(SettingsWindowLayout.MinimumClientWidth),
            bottom = Scale(SettingsWindowLayout.MinimumClientHeight),
        };
        AdjustWindowRect(ref minimum, Style, false);

        MINMAXINFO limits = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        limits.ptMinTrackSize.x = minimum.right - minimum.left;
        limits.ptMinTrackSize.y = minimum.bottom - minimum.top;
        Marshal.StructureToPtr(limits, lParam, fDeleteOld: false);
    }

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
        SetWindowText(_signInOut, view.SignedIn ? SettingsPresenter.SignOutLabel : SettingsPresenter.SignInLabel);

        SetWindowText(_updateLabel, view.Update);
        EnableWindow(_update, view.CanUpdate);
        EnableWindow(_checkForUpdates, _actions.CheckForUpdate is not null);

        if (_accountTone != view.AccountTone || _connectionTone != view.ConnectionTone)
        {
            _accountTone = view.AccountTone;
            _connectionTone = view.ConnectionTone;
            InvalidateRect(_accountOrb, IntPtr.Zero, true);
            InvalidateRect(_connectionOrb, IntPtr.Zero, true);
        }

        if (!_shown.SequenceEqual(view.Sessions))
        {
            FillSessions(view.Sessions);
        }

        if (!_layout.ColumnWidths.SequenceEqual(ReadColumnWidths()))
        {
            SchedulePersist();
        }

        if (reread)
        {
            Check(_startAtLogon, Ask(_actions.ReadStartAtLogon));
        }
    }

    private void FillSessions(IReadOnlyList<SessionRow> rows)
    {
        int top = (int)SendMessage(_sessions, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
        SendMessage(_sessions, LVM_DELETEALLITEMS, IntPtr.Zero, IntPtr.Zero);

        IReadOnlyList<SessionRow> sorted = SessionTableSorter.Sort(rows, _sortColumn, _sortDescending);

        for (int rowIndex = 0; rowIndex < sorted.Count; rowIndex++)
        {
            SessionRow row = sorted[rowIndex];
            string[] cells = [row.Name, row.Source, row.Folder, row.Status, row.Activity];

            for (int columnIndex = 0; columnIndex < cells.Length; columnIndex++)
            {
                var item = new LVITEM
                {
                    mask = LVIF_TEXT,
                    iItem = rowIndex,
                    iSubItem = columnIndex,
                    pszText = cells[columnIndex],
                    cchTextMax = cells[columnIndex].Length,
                };

                SendMessageListItem(
                    _sessions,
                    columnIndex == 0 ? LVM_INSERTITEMW : LVM_SETITEMW,
                    IntPtr.Zero,
                    ref item);
            }
        }

        if (top > 0 && sorted.Count > 0)
        {
            SendMessage(_sessions, LVM_ENSUREVISIBLE, (IntPtr)Math.Min(top, sorted.Count - 1), IntPtr.Zero);
        }

        _shown = [.. rows];
        ShowWindow(
            _sessionsEmpty,
            _page == SettingsPage.Sessions && rows.Count == 0 ? SW_SHOW : SW_HIDE);
    }

    private void OnColumnClicked(int column)
    {
        if (!Enum.IsDefined((SessionTableColumn)column))
        {
            return;
        }

        SessionTableColumn selected = (SessionTableColumn)column;
        _sortDescending = selected == _sortColumn ? !_sortDescending : false;
        _sortColumn = selected;
        UpdateSortIndicator();
        FillSessions(_shown);
        SchedulePersist();
    }

    private void UpdateSortIndicator()
    {
        IntPtr header = SendMessage(_sessions, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);

        if (header == IntPtr.Zero)
        {
            return;
        }

        for (int index = 0; index < 5; index++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            SendMessageHeaderItem(header, HDM_GETITEMW, (IntPtr)index, ref item);
            item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);

            if (index == (int)_sortColumn)
            {
                item.fmt |= _sortDescending ? HDF_SORTDOWN : HDF_SORTUP;
            }

            SendMessageHeaderItem(header, HDM_SETITEMW, (IntPtr)index, ref item);
        }
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_COMMAND:
                OnCommand((int)((long)wParam & 0xFFFF));
                return IntPtr.Zero;

            case WM_NOTIFY:
                NMHDR notification = Marshal.PtrToStructure<NMHDR>(lParam);
                if (notification.hwndFrom == _changeHistory &&
                    notification.code is NM_CLICK or NM_RETURN)
                {
                    OffThread(_actions.OpenChangeHistory);
                    return IntPtr.Zero;
                }

                if (notification.hwndFrom == _sessions && notification.code == LVN_COLUMNCLICK)
                {
                    OnColumnClicked(Marshal.PtrToStructure<NM_LISTVIEW>(lParam).iSubItem);
                    return IntPtr.Zero;
                }

                if (notification.hwndFrom == _tabs && notification.code == TCN_SELCHANGE)
                {
                    int selected = (int)SendMessage(_tabs, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                    if (Enum.IsDefined((SettingsPage)selected))
                    {
                        ShowPage((SettingsPage)selected);
                    }

                    return IntPtr.Zero;
                }

                return DefWindowProc(window, message, wParam, lParam);

            case WM_TIMER:
                if ((int)wParam == PersistTimer)
                {
                    PersistLayout();
                }
                else
                {
                    Refresh(reread: false);
                }

                return IntPtr.Zero;

            case WM_SIZE:
                int sizeState = (int)wParam;

                if (sizeState != SIZE_MINIMIZED)
                {
                    LayoutControls();
                }

                if (sizeState == SIZE_RESTORED)
                {
                    RememberRestoredSize();
                    SchedulePersist();
                }

                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
                SetMinimumSize(lParam);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                // Painted here rather than by a class brush, because the class brush is
                // fixed when the class is registered and the theme is not.
                if (GetClientRect(window, out RECT client))
                {
                    FillRect(wParam, ref client, _theme.SurfaceBrush);
                }

                return 1;

            case WM_CTLCOLORSTATIC:
                // Labels and the checkbox paint their own background otherwise, leaving
                // light rectangles on a dark surface. The version line is the one thing
                // here that is deliberately quieter than the rest.
                SetBkMode(wParam, TRANSPARENT);
                SetTextColor(
                    wParam,
                    lParam == _accountOrb ? _theme.StatusColor(_accountTone)
                    : lParam == _connectionOrb ? _theme.StatusColor(_connectionTone)
                    : lParam == _versionLabel || lParam == _updateLabel ? _theme.SecondaryText
                    : _theme.Text);
                SetBkColor(wParam, _theme.Surface);
                return _theme.SurfaceBrush;

            case WM_SETTINGCHANGE:
                // Broadcast for every system setting there is, so the payload has to be
                // read. Anything else would rebuild the theme on unrelated changes.
                if (IsColorSetChange(lParam))
                {
                    OnThemeChanged();
                }

                return DefWindowProc(window, message, wParam, lParam);

            case WM_THEMECHANGED:
                OnThemeChanged();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                OnDpiChanged((int)((long)wParam & 0xFFFF), lParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                Close();
                return IntPtr.Zero;

            case WM_DESTROY:
                Destroy();
                return IntPtr.Zero;

            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Whether a <c>WM_SETTINGCHANGE</c> is the one that says the colours moved.
    /// <para>
    /// <c>lParam</c> is a native string and may be null, which is why it is read by
    /// hand rather than by the window procedure's signature — every message this window
    /// receives arrives through that same one.
    /// </para>
    /// </summary>
    private static bool IsColorSetChange(IntPtr lParam) =>
        lParam != IntPtr.Zero &&
        string.Equals(Marshal.PtrToStringUni(lParam), "ImmersiveColorSet", StringComparison.Ordinal);

    /// <summary>
    /// Re-reads the light/dark preference and puts the whole window into it, without
    /// closing anything. Somebody who changes the system theme while this is open sees
    /// it follow, which is what every other Windows 11 window does.
    /// </summary>
    private void OnThemeChanged()
    {
        Theme previous = _theme;

        _theme = Theme.Current();

        _theme.ApplyToWindow(_window);
        Theme.AllowSystemThemedMenus();

        foreach (IntPtr control in _controls)
        {
            _theme.ApplyToControl(control);
        }

        // Everything is repainted before the old brushes are destroyed: a brush that is
        // still selected into a device context must not be deleted underneath it.
        RedrawWindow(
            _window,
            IntPtr.Zero,
            IntPtr.Zero,
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);

        previous.Dispose();
    }

    /// <summary>
    /// Follows the window onto a monitor at another scale.
    /// <para>
    /// Windows supplies the rectangle it wants the window at, and a per-monitor-v2
    /// process is expected to take it: ignoring it leaves the window at its old
    /// physical size, which on a 150% monitor is two thirds of what it should be. The
    /// children are then laid out again from the 96-DPI rectangles they were created
    /// with, and the fonts remade, because a font is sized in pixels and those have
    /// just changed.
    /// </para>
    /// </summary>
    private void OnDpiChanged(int dpi, IntPtr suggested)
    {
        int[] columnWidths = ReadColumnWidths();
        _dpi = dpi == 0 ? 96 : dpi;

        CreateFonts();

        if (suggested != IntPtr.Zero)
        {
            RECT bounds = Marshal.PtrToStructure<RECT>(suggested);

            SetWindowPos(
                _window,
                IntPtr.Zero,
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SWP_NOZORDER);
        }

        foreach (IntPtr control in _controls)
        {
            SetFont(control, FontFor(control));
        }

        SetColumnWidths(columnWidths);
        LayoutControls();
        RedrawWindow(_window, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
    }

    /// <summary>
    /// Which of the three weights a control was created with. Asked again rather than
    /// remembered per control: there are two exceptions and they are both named here.
    /// </summary>
    private IntPtr FontFor(IntPtr control)
    {
        if (control == _accountLabel || control == _sessionsLabel)
        {
            return _strongFont;
        }

        return control == _versionLabel || control == _changeHistory ? _captionFont : _font;
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

            // Off the message thread, like every other action here: this downloads
            // thirty megabytes and then runs a program, and doing it on the thread that
            // pumps the tray's messages would freeze the icon and the window with it.
            // The refresh timer picks the progress up a second later.
            case IdUpdate:
                OffThread(_actions.Update);
                break;

            case IdCheckForUpdates:
                if (_actions.CheckForUpdate is not null)
                {
                    OffThread(_actions.CheckForUpdate);
                }
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
            KillTimer(_window, PersistTimer);
        }

        DeleteFonts();

        _window = IntPtr.Zero;
        _controls.Clear();
        _statusControls.Clear();
        _sessionControls.Clear();
        _settingsControls.Clear();
        _tabs = IntPtr.Zero;
        _accountOrb = IntPtr.Zero;
        _accountLabel = IntPtr.Zero;
        _connectionOrb = IntPtr.Zero;
        _connectionLabel = IntPtr.Zero;
        _sessionsLabel = IntPtr.Zero;
        _signInOut = IntPtr.Zero;
        _sessions = IntPtr.Zero;
        _sessionsEmpty = IntPtr.Zero;
        _wrapShortcut = IntPtr.Zero;
        _startAtLogon = IntPtr.Zero;
        _versionLabel = IntPtr.Zero;
        _changeHistory = IntPtr.Zero;
        _updateLabel = IntPtr.Zero;
        _update = IntPtr.Zero;
        _checkForUpdates = IntPtr.Zero;
        _openLogs = IntPtr.Zero;
        _sendFeedback = IntPtr.Zero;
        _close = IntPtr.Zero;
        _shown = [];
    }

    /// <summary>
    /// Frees the three fonts. Called on close and again on every DPI change, because a
    /// font is created at a pixel size and the ones for the old scale are dead the
    /// moment the new ones exist.
    /// </summary>
    private void DeleteFonts()
    {
        Delete(ref _font);
        Delete(ref _strongFont);
        Delete(ref _captionFont);

        static void Delete(ref IntPtr font)
        {
            if (font != IntPtr.Zero)
            {
                DeleteObject(font);
                font = IntPtr.Zero;
            }
        }
    }
}
