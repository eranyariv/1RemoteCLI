using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

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
/// Runs its own message loop on its own thread. A tray icon needs a pumping window,
/// the agent's main thread is busy awaiting a pipe server, and marrying the two would
/// mean either a WinForms application context around the whole agent or a hand-rolled
/// synchronisation context. A thread that owns the icon and nothing else is smaller
/// than either and cannot deadlock the part that matters.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly string _machineName;
    private readonly Action _onSignIn;
    private readonly Action _onShowSessions;
    private readonly Action _onOpenLogs;
    private readonly Action _onQuit;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private NotifyIcon? _icon;
    private ToolStripMenuItem? _signIn;
    private ApplicationContext? _context;
    private readonly Dictionary<AgentState, Icon> _icons = [];

    private AgentState _state = AgentState.Reconnecting;
    private int _sessions;

    public TrayIcon(
        string machineName,
        Action onSignIn,
        Action onShowSessions,
        Action onOpenLogs,
        Action onQuit)
    {
        _machineName = machineName ?? string.Empty;
        _onSignIn = onSignIn ?? throw new ArgumentNullException(nameof(onSignIn));
        _onShowSessions = onShowSessions ?? throw new ArgumentNullException(nameof(onShowSessions));
        _onOpenLogs = onOpenLogs ?? throw new ArgumentNullException(nameof(onOpenLogs));
        _onQuit = onQuit ?? throw new ArgumentNullException(nameof(onQuit));

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "1remote tray",
        };

        // Required: the shell's drag-and-drop and common dialogs are single-threaded
        // apartment, and a NotifyIcon on an MTA thread fails in ways that look random.
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
    /// the session registry's change event, neither of which knows this exists.
    /// </para>
    /// </summary>
    public void Update(AgentState state, int sessions)
    {
        _state = state;
        _sessions = sessions;

        Post(Render);
    }

    public void Dispose()
    {
        Post(() =>
        {
            if (_icon is not null)
            {
                // Explicitly, and before anything else: an unhidden NotifyIcon leaves a
                // ghost in the tray that survives the process and only disappears when
                // the user hovers over it.
                _icon.Visible = false;
                _icon.Dispose();
            }

            foreach (Icon icon in _icons.Values)
            {
                icon.Dispose();
            }

            _context?.ExitThread();
        });

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Pump()
    {
        var menu = new ContextMenuStrip();

        _signIn = new ToolStripMenuItem("Sign in", null, (_, _) => _onSignIn());
        menu.Items.Add(_signIn);
        menu.Items.Add(new ToolStripMenuItem("Show sessions", null, (_, _) => _onShowSessions()));
        menu.Items.Add(new ToolStripMenuItem("Open logs", null, (_, _) => _onOpenLogs()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => _onQuit()));

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Double-click is what people try first, and it should do the most useful
        // thing rather than nothing.
        _icon.DoubleClick += (_, _) => _onShowSessions();

        Render();

        _ready.Set();

        _context = new ApplicationContext();
        Application.Run(_context);
    }

    private void Render()
    {
        if (_icon is null)
        {
            return;
        }

        TrayPresentation view = TrayPresenter.Present(_state, _sessions, _machineName);

        _icon.Icon = IconFor(view.Badge);
        _icon.Text = view.Tooltip;

        if (_signIn is not null)
        {
            _signIn.Enabled = view.SignInEnabled;
        }
    }

    /// <summary>Runs an action on the icon's thread, or drops it if there is no icon yet.</summary>
    private void Post(Action action)
    {
        if (_icon is not { } icon)
        {
            return;
        }

        try
        {
            if (icon.ContextMenuStrip is { InvokeRequired: true } menu)
            {
                menu.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The tray is decoration. It must never take the agent down with it.
        }
    }

    /// <summary>
    /// The icon for a state, built once and kept.
    /// <para>
    /// Sized from the shell rather than hardcoded to 16: on a 125% or 150% display
    /// Windows asks for 20 or 24 pixels, and answering with a stretched 16 is why a
    /// tray icon looks soft next to its neighbours.
    /// </para>
    /// </summary>
    private Icon IconFor(AgentState state)
    {
        if (_icons.TryGetValue(state, out Icon? cached))
        {
            return cached;
        }

        Icon icon = TrayArtwork.Create(state, SystemInformation.SmallIconSize.Width);
        _icons[state] = icon;

        return icon;
    }
}
