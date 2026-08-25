using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Update;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>
/// The agent's only face.
/// <para>
/// For most users this icon is the entire product on the desktop side — they will
/// never run <c>1remote status</c>, and the first time they look at it will be the
/// moment their phone stopped seeing a machine. So it has to answer, at a glance and
/// without being clicked, the only question they have: is this working.
/// </para>
/// <para>
/// Talks to the shell directly rather than through <c>NotifyIcon</c>. Windows Forms
/// was referenced for this one control and dragged the whole Windows Desktop runtime
/// into every download — around 40 MB for an icon and a popup menu (issue #46).
/// </para>
/// <para>
/// Runs its own message loop on its own thread. A tray icon needs a window to deliver
/// its callbacks to and a thread pumping messages for it, and the agent's main thread
/// is busy awaiting a pipe server. A thread that owns the icon and nothing else cannot
/// deadlock the part that matters.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// The shell's whole notion of identity here is the pair (window handle, id), and
    /// the handle is already unique per process, so one id is enough.
    /// </summary>
    private const int IconId = 1;
    private const int DelayedMenuTimer = 1;

    private readonly string _machineName;
    private readonly Dictionary<TrayCommand, Action> _commands;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Dictionary<(AgentState State, int Sessions), Icon> _icons = [];

    /// <summary>
    /// Held in a field because the window class keeps the pointer for as long as the
    /// class exists. A delegate that only exists as an argument is collected as soon as
    /// the call returns, and the next message dispatched into it takes the process down.
    /// </summary>
    private readonly WndProc _wndProc;

    private IntPtr _window;
    private bool _iconAdded;
    private int _delayedMenuX;
    private int _delayedMenuY;

    /// <summary>
    /// Explorer broadcasts this when it restarts, and every tray icon has to add itself
    /// back. Without it the agent survives an Explorer crash but vanishes from the tray
    /// until it is restarted, which looks exactly like the agent having crashed.
    /// </summary>
    private int _taskbarCreated;

    private volatile TrayState _current = new(AgentState.Reconnecting, 0, null);

    /// <summary>
    /// What the shell was last told, so an update that changes nothing costs nothing.
    /// <para>
    /// The session count arrives on the registry's change event, which is quiet — but
    /// the connection state and the signed-in account feed the same refresh, and every
    /// one of them asking the shell to repaint an identical icon is a repaint the tray
    /// does not need. Compared by value, so this stays correct as the state grows.
    /// </para>
    /// </summary>
    private TrayState? _shown;

    /// <summary>
    /// Created on this thread, on demand, and kept: reopening has to focus the window
    /// that is already there rather than stack another one.
    /// </summary>
    private readonly SettingsWindow _settings;

    public TrayIcon(
        string machineName,
        Action onShowSessions,
        Action onQuit,
        SettingsActions settings)
    {
        _machineName = machineName ?? string.Empty;

        _commands = new Dictionary<TrayCommand, Action>
        {
            [TrayCommand.ShowSessions] = onShowSessions ?? throw new ArgumentNullException(nameof(onShowSessions)),
            [TrayCommand.Quit] = onQuit ?? throw new ArgumentNullException(nameof(onQuit)),

            // The same action the settings window's button runs. Two ways in, one thing
            // done: the menu is where somebody who has just seen the icon looks, and the
            // window is where somebody who was already reading the version is.
            [TrayCommand.Update] = (settings ?? throw new ArgumentNullException(nameof(settings))).Update,
        };

        _settings = new SettingsWindow(settings);

        _wndProc = HandleMessage;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "1remote tray",
        };

        // The shell's context menus and common dialogs are single-threaded apartment,
        // and a tray icon on an MTA thread fails in ways that look random.
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Starts the icon and waits for it to exist, so early updates are not lost.</summary>
    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Tells the tray what the agent is doing.
    /// <para>
    /// Callable from any thread — it is called from the hub's reconnect loop and from
    /// the session registry's change event, neither of which knows this exists. The
    /// state is swapped in one go and the icon's own thread is asked to redraw it,
    /// because touching a window from another thread is how tray icons come to be wedged.
    /// </para>
    /// </summary>
    public void Update(AgentState state, int sessions, string? account = null, UpdateStatus update = default)
    {
        _current = new TrayState(state, sessions, account, update);

        Ask(WM_TRAY_UPDATE);
    }

    public void Dispose()
    {
        Ask(WM_TRAY_QUIT);

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Ask(int message)
    {
        IntPtr window = _window;

        if (window != IntPtr.Zero)
        {
            PostMessage(window, message, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Pump()
    {
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

            // Before any window or menu exists. This is what lets the tray menu follow
            // the user's light/dark preference: Windows draws that menu, not us, and it
            // decides at creation which theme to draw it in (issue #105).
            Theme.AllowSystemThemedMenus();

            _window = CreateHiddenWindow();

            AddIcon();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // The tray is decoration. An agent with no icon still relays sessions, and
            // taking the process down over a failed icon would cost the user the thing
            // they actually wanted.
            _ready.Set();
            return;
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            // First refusal to the settings window, which is what gives it Tab and
            // Escape. Returns true when it consumed the message, and dispatching it
            // anyway would deliver the keystroke twice.
            if (_settings.Handles(ref message))
            {
                continue;
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        Cleanup();
    }

    /// <summary>
    /// Creates the window the shell delivers callbacks to.
    /// <para>
    /// A real top-level window rather than a message-only one, and deliberately: a
    /// popup menu will not dismiss correctly unless its owner can be brought to the
    /// foreground, and an <c>HWND_MESSAGE</c> window cannot be. It is never shown, and
    /// <c>WS_EX_TOOLWINDOW</c> keeps it out of the taskbar and Alt-Tab.
    /// </para>
    /// </summary>
    private IntPtr CreateHiddenWindow()
    {
        IntPtr instance = GetModuleHandle(null);

        // Unique per process: registering a class name that already exists fails, and a
        // second agent starting up must not be able to break the first.
        string className = $"1RemoteCLI.Tray.{Environment.ProcessId}";

        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException(
                $"Registering the tray window class failed: {Marshal.GetLastWin32Error()}");
        }

        IntPtr window = CreateWindowEx(
            WS_EX_TOOLWINDOW,
            className,
            "1RemoteCLI",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        return window == IntPtr.Zero
            ? throw new InvalidOperationException(
                $"Creating the tray window failed: {Marshal.GetLastWin32Error()}")
            : window;
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        // Not a constant, so it cannot be switched on: Windows allocates the number at
        // runtime and it differs per session.
        if (message == _taskbarCreated)
        {
            _iconAdded = false;
            AddIcon();

            return IntPtr.Zero;
        }

        switch (message)
        {
            case WM_TRAYICON:
                OnIconClicked(wParam, lParam);
                return IntPtr.Zero;

            case WM_TIMER when (int)wParam == DelayedMenuTimer:
                KillTimer(window, (IntPtr)DelayedMenuTimer);
                ShowMenu(_delayedMenuX, _delayedMenuY);
                return IntPtr.Zero;

            case WM_TRAY_UPDATE:
                Render();
                return IntPtr.Zero;

            case WM_TRAY_SETTINGS:
                _settings.Show();
                return IntPtr.Zero;

            case WM_TRAY_QUIT:
                _settings.Close();
                DestroyWindow(window);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Unpacks a version 4 callback: the anchor point in <paramref name="wParam"/>, the
    /// event in the low half of <paramref name="lParam"/>. This packing is the reason
    /// for asking for version 4 at all — the shell supplies the point the menu belongs
    /// at, including when the tray was opened from the keyboard and there is no cursor
    /// to ask about.
    /// </summary>
    private void OnIconClicked(IntPtr wParam, IntPtr lParam)
    {
        int x = SignedLowWord(wParam);
        int y = SignedHighWord(wParam);

        switch (TrayIconInteraction.ActionFor(LowWord(lParam)))
        {
            case TrayIconAction.DelayMenu:
                _delayedMenuX = x;
                _delayedMenuY = y;
                KillTimer(_window, (IntPtr)DelayedMenuTimer);
                if (SetTimer(
                        _window,
                        (IntPtr)DelayedMenuTimer,
                        GetDoubleClickTime(),
                        IntPtr.Zero) == IntPtr.Zero)
                {
                    ShowMenu(x, y);
                }

                break;

            case TrayIconAction.ShowMenu:
                KillTimer(_window, (IntPtr)DelayedMenuTimer);
                ShowMenu(x, y);
                break;

            case TrayIconAction.ShowSettings:
                KillTimer(_window, (IntPtr)DelayedMenuTimer);
                _settings.Show();
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        IReadOnlyList<TrayMenuItem> items = TrayMenu.Build(Present());
        IntPtr menu = CreatePopupMenu();

        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                TrayMenuItem item = items[i];

                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                // One past the index: TrackPopupMenuEx returns 0 for "nothing chosen",
                // so no item may own that value.
                AppendMenu(menu, MF_STRING | (item.Enabled ? 0 : MF_GRAYED), i + 1, item.Text);

                if (item.IsDefault)
                {
                    // By position rather than by id, because a separator has no id and
                    // the two numbering schemes diverge as soon as one is appended.
                    SetMenuDefaultItem(menu, i, 1);
                }
            }

            // Required, and easy to miss. A popup menu whose owner is not in the
            // foreground stays on screen after the user clicks elsewhere, leaving what
            // looks like a stuck menu over their desktop.
            SetForegroundWindow(_window);

            int chosen = TrackPopupMenuEx(
                menu,
                TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                x,
                y,
                _window,
                IntPtr.Zero);

            // The other half of the same defect: with no message to process, the menu is
            // not taken down until this window happens to receive one.
            PostMessage(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (chosen > 0 && chosen <= items.Count)
            {
                Invoke(items[chosen - 1].Command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Runs a menu action, off the message loop.
    /// <para>
    /// On another thread because these open browsers, launch mail clients and stop the
    /// agent, and any of them blocking would freeze the loop that owns the icon —
    /// leaving the tray unresponsive at exactly the moment the user has asked it to do
    /// something.
    /// </para>
    /// <para>
    /// The settings window is the exception, and has to be: a window belongs to the
    /// thread that created it, and one created on a thread-pool thread would have
    /// nothing pumping its messages. It is asked for by posting back to this thread
    /// instead.
    /// </para>
    /// </summary>
    private void Invoke(TrayCommand command)
    {
        if (command == TrayCommand.Settings)
        {
            Ask(WM_TRAY_SETTINGS);
            return;
        }

        if (!_commands.TryGetValue(command, out Action? action))
        {
            return;
        }

        _ = Task.Run(action);
    }

    private void AddIcon()
    {
        if (_iconAdded)
        {
            return;
        }

        TrayState state = _current;
        NOTIFYICONDATA data = Describe(state, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            return;
        }

        _iconAdded = true;
        _shown = state;

        // Opt in to the modern callback packing. Has to follow the add, and is what
        // makes WM_CONTEXTMENU arrive with the point the menu should appear at.
        NOTIFYICONDATA version = Blank();
        version.uVersion = NOTIFYICON_VERSION_4;

        Shell_NotifyIcon(NIM_SETVERSION, ref version);
    }

    private TrayPresentation Present()
    {
        TrayState state = _current;

        return TrayPresenter.Present(state.State, state.Sessions, _machineName, state.Account, state.Update);
    }

    private void Render()
    {
        TrayState state = _current;

        if (!_iconAdded || state == _shown)
        {
            return;
        }

        NOTIFYICONDATA data = Describe(state, NIF_ICON | NIF_TIP | NIF_SHOWTIP);

        Shell_NotifyIcon(NIM_MODIFY, ref data);

        _shown = state;
    }

    private NOTIFYICONDATA Describe(TrayState state, int flags)
    {
        TrayPresentation view = TrayPresenter.Present(
            state.State,
            state.Sessions,
            _machineName,
            state.Account);

        NOTIFYICONDATA data = Blank();

        data.uFlags = flags;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = IconFor(view.Badge, state.Sessions).Handle;
        data.szTip = view.Tooltip;

        return data;
    }

    /// <summary>
    /// The identifying fields, and empty strings for the rest.
    /// <para>
    /// The strings are fixed-length buffers the marshaller copies into, and it will not
    /// copy a null. Leaving them unset throws before the shell is ever called.
    /// </para>
    /// </summary>
    private NOTIFYICONDATA Blank() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void Cleanup()
    {
        KillTimer(_window, (IntPtr)DelayedMenuTimer);

        if (_iconAdded)
        {
            // Explicitly: an icon the shell was never told about leaves a ghost in the
            // tray that survives the process and only disappears when the user happens
            // to hover over it.
            NOTIFYICONDATA data = Blank();

            Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconAdded = false;
        }

        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
        _window = IntPtr.Zero;
        _shown = null;
    }

    /// <summary>
    /// The icon for a state and a session count, built once and kept.
    /// <para>
    /// Sized from the shell rather than hardcoded to 16: on a 125% or 150% display
    /// Windows asks for 20 or 24 pixels, and answering with a stretched 16 is why a
    /// tray icon looks soft next to its neighbours.
    /// </para>
    /// <para>
    /// The count is clamped into the cache key rather than used raw, because every
    /// count past the ceiling draws the same "&gt;9" and a raw key would quietly hold
    /// one identical icon per session ever started.
    /// </para>
    /// </summary>
    private Icon IconFor(AgentState state, int sessions)
    {
        int count = Math.Clamp(sessions, 0, TrayArtwork.CountCeiling);

        if (_icons.TryGetValue((state, count), out Icon? cached))
        {
            return cached;
        }

        Icon icon = TrayArtwork.Create(state, Math.Max(8, GetSystemMetrics(SM_CXSMICON)), count);
        _icons[(state, count)] = icon;

        return icon;
    }

    private static int LowWord(IntPtr value) => (int)((long)value & 0xFFFF);

    private static int SignedLowWord(IntPtr value) => (short)((long)value & 0xFFFF);

    private static int SignedHighWord(IntPtr value) => (short)(((long)value >> 16) & 0xFFFF);

    /// <summary>
    /// Everything the tray shows, swapped as one value.
    /// <para>
    /// A record rather than three fields because <see cref="Update"/> is called from
    /// other threads: three separate writes can be read back as a mixture of two
    /// updates, and "connected, 0 sessions" is a combination that would send the user
    /// looking for a fault that never existed.
    /// </para>
    /// </summary>
    private sealed record TrayState(AgentState State, int Sessions, string? Account, UpdateStatus Update = default);
}

internal enum TrayIconAction
{
    None,
    DelayMenu,
    ShowMenu,
    ShowSettings,
}

/// <summary>Maps shell notification codes to tray behavior without requiring a live shell.</summary>
internal static class TrayIconInteraction
{
    public static TrayIconAction ActionFor(int notification) =>
        notification switch
        {
            NIN_SELECT => TrayIconAction.DelayMenu,
            NIN_KEYSELECT or WM_CONTEXTMENU => TrayIconAction.ShowMenu,
            WM_LBUTTONDBLCLK => TrayIconAction.ShowSettings,
            _ => TrayIconAction.None,
        };
}
