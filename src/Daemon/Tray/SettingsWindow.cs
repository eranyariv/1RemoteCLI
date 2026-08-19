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

    private const int ClientWidth = 480;

    /// <summary>Between two groups. Inside one, things sit <see cref="Tight"/> apart.</summary>
    private const int Gap = 20;

    private const int Tight = 8;

    /// <summary>One line of body text at 14px, with room for descenders.</summary>
    private const int RowHeight = 20;

    private const int ButtonHeight = 32;

    private const int SessionsHeight = 140;

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
    /// Every child, with the rectangle it was laid out at in 96-DPI units.
    /// <para>
    /// Kept so the window can be laid out again at a different scale. Under
    /// per-monitor-v2 awareness — which <c>app.manifest</c> asks for — dragging the
    /// window to a monitor at another scale sends <c>WM_DPICHANGED</c> and the window
    /// is expected to resize itself; without this list the only thing it could do is
    /// stretch, which is the blur that awareness was turned on to remove.
    /// </para>
    /// </summary>
    private readonly List<(IntPtr Control, int X, int Y, int Width, int Height)> _controls = [];

    private bool _classRegistered;
    private int _dpi = 96;

    private IntPtr _window;
    private IntPtr _font;
    private IntPtr _strongFont;
    private IntPtr _captionFont;
    private Theme _theme = Theme.Current();
    private IntPtr _accountLabel;
    private IntPtr _connectionLabel;
    private IntPtr _sessionsLabel;
    private IntPtr _signInOut;
    private IntPtr _sessions;
    private IntPtr _startAtLogon;
    private IntPtr _versionLabel;

    /// <summary>Where the list box sits, in 96-DPI units, so its hairline can be drawn.</summary>
    private (int X, int Y, int Width, int Height) _sessionsBounds;

    /// <summary>
    /// The height the layout came out at, so the frame can be sized to fit it rather
    /// than to a constant somebody has to remember to update.
    /// </summary>
    private int _clientHeight = 432;

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
            right = Scale(ClientWidth),
            bottom = Scale(_clientHeight),
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
        const int signInWidth = 112;
        int y = Margin;

        // Row one: who, and the button that changes it. The button is on the same line
        // because signing in or out is the one thing this line ever leads to, and it is
        // set in Body Strong because it is the answer to the question the window was
        // opened to ask.
        int textWidth = content - signInWidth - Gap;

        _accountLabel = Static(instance, Margin, y, textWidth, RowHeight, font: _strongFont);
        _signInOut = Button(instance, IdSignInOut, ClientWidth - Margin - signInWidth, y, signInWidth, ButtonHeight);

        // Directly under the account, inside the same group, because signed out is one
        // of the reasons the phone cannot see this machine. Two rows tall and without
        // the ellipsis style, so the longest of these sentences wraps rather than losing
        // its ending — which in one case is the part that says sessions still work.
        y += RowHeight + Tight;
        _connectionLabel = Static(instance, Margin, y, textWidth, RowHeight * 2, style: SS_LEFT);
        y += (RowHeight * 2) + Gap;

        _sessionsLabel = Static(
            instance,
            Margin,
            y,
            content,
            RowHeight,
            SettingsPresenter.SessionsLabel,
            font: _strongFont);
        y += RowHeight + Tight;

        // No WS_BORDER. That border is drawn by Windows in a system colour that stays
        // light in dark mode; the hairline is drawn in WM_PAINT instead, in the theme's
        // own stroke colour.
        _sessionsBounds = (Margin, y, content, SessionsHeight);
        _sessions = Create(
            instance,
            "LISTBOX",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_TABSTOP | LBS_NOSEL | LBS_NOINTEGRALHEIGHT,
            Margin,
            y,
            content,
            SessionsHeight,
            IntPtr.Zero);
        y += SessionsHeight + Gap;

        _startAtLogon = Create(
            instance,
            "BUTTON",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
            Margin,
            y,
            content,
            RowHeight + 4,
            IdStartAtLogon,
            SettingsPresenter.StartAtLogonLabel);
        y += RowHeight + 4 + Gap;

        // The one row of actions. Wrapping a shortcut lives here rather than in the
        // tray menu (issue #66): it is a rare, deliberate act, and the tray menu is for
        // the things that are needed in a hurry.
        const int wrapWidth = 188;
        const int logsWidth = 104;
        int feedbackX = Margin + wrapWidth + Tight + logsWidth + Tight;

        Button(instance, IdWrapShortcut, Margin, y, wrapWidth, ButtonHeight, SettingsPresenter.WrapShortcutLabel);
        Button(instance, IdOpenLogs, Margin + wrapWidth + Tight, y, logsWidth, ButtonHeight, SettingsPresenter.OpenLogsLabel);
        Button(
            instance,
            IdSendFeedback,
            feedbackX,
            y,
            ClientWidth - Margin - feedbackX,
            ButtonHeight,
            SettingsPresenter.SendFeedbackLabel);
        y += ButtonHeight + Gap;

        // The version is the first thing any bug report needs, so it sits next to the
        // button that sends one. Caption weight and the secondary colour: it is true,
        // but it is not why anybody opened this.
        const int closeWidth = 104;

        _versionLabel = Static(
            instance,
            Margin,
            y + ((ButtonHeight - RowHeight) / 2),
            content - closeWidth - Gap,
            RowHeight,
            font: _captionFont);

        Button(
            instance,
            IdClose,
            ClientWidth - Margin - closeWidth,
            y,
            closeWidth,
            ButtonHeight,
            SettingsPresenter.CloseLabel,
            BS_DEFPUSHBUTTON);

        _clientHeight = y + ButtonHeight + Margin;
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

        _controls.Add((control, x, y, width, height));

        SetFont(control, font);

        // Buttons, checkboxes and the list box draw themselves, so their colours cannot
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

            case WM_ERASEBKGND:
                // Painted here rather than by a class brush, because the class brush is
                // fixed when the class is registered and the theme is not.
                if (GetClientRect(window, out RECT client))
                {
                    FillRect(wParam, ref client, _theme.SurfaceBrush);
                }

                return 1;

            case WM_PAINT:
                OnPaint(window);
                return IntPtr.Zero;

            case WM_CTLCOLORSTATIC:
                // Labels and the checkbox paint their own background otherwise, leaving
                // light rectangles on a dark surface. The version line is the one thing
                // here that is deliberately quieter than the rest.
                SetBkMode(wParam, TRANSPARENT);
                SetTextColor(wParam, lParam == _versionLabel ? _theme.SecondaryText : _theme.Text);
                SetBkColor(wParam, _theme.Surface);
                return _theme.SurfaceBrush;

            case WM_CTLCOLORLISTBOX:
                // One step off the surface, which is how Fluent separates a list from
                // the window it sits in now that the border is a hairline.
                SetTextColor(wParam, _theme.Text);
                SetBkColor(wParam, _theme.Layer);
                return _theme.LayerBrush;

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
                DestroyWindow(window);
                return IntPtr.Zero;

            case WM_DESTROY:
                Destroy();
                return IntPtr.Zero;

            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Draws the hairline around the session list.
    /// <para>
    /// A <c>WS_BORDER</c> would be one call instead of this, and would stay light in
    /// dark mode: that border is drawn by Windows in a system colour, and there is no
    /// message asking us what colour it should be.
    /// </para>
    /// </summary>
    private void OnPaint(IntPtr window)
    {
        IntPtr context = BeginPaint(window, out PAINTSTRUCT paint);

        if (context != IntPtr.Zero)
        {
            (int x, int y, int width, int height) = _sessionsBounds;

            var frame = new RECT
            {
                left = Scale(x) - 1,
                top = Scale(y) - 1,
                right = Scale(x + width) + 1,
                bottom = Scale(y + height) + 1,
            };

            FrameRect(context, ref frame, _theme.BorderBrush);
        }

        EndPaint(window, ref paint);
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

        foreach ((IntPtr control, _, _, _, _) in _controls)
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

        foreach ((IntPtr control, int x, int y, int width, int height) in _controls)
        {
            MoveWindow(control, Scale(x), Scale(y), Scale(width), Scale(height), false);
            SetFont(control, FontFor(control));
        }

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

        return control == _versionLabel ? _captionFont : _font;
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

        DeleteFonts();

        _window = IntPtr.Zero;
        _controls.Clear();
        _accountLabel = IntPtr.Zero;
        _connectionLabel = IntPtr.Zero;
        _sessionsLabel = IntPtr.Zero;
        _signInOut = IntPtr.Zero;
        _sessions = IntPtr.Zero;
        _startAtLogon = IntPtr.Zero;
        _versionLabel = IntPtr.Zero;
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
