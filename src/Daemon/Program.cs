using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Identity.Client;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Auth;
using Microsoft.Extensions.Logging;
using OneRemoteCli.Daemon.Cli;
using OneRemoteCli.Daemon.Diagnostics;
using OneRemoteCli.Daemon.Hub;
using OneRemoteCli.Daemon.Install;
using OneRemoteCli.Daemon.Pty;
using OneRemoteCli.Daemon.Shell;
using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Diagnostics;

namespace OneRemoteCli.Daemon;

/// <summary>
/// Entry point for <c>1remote.exe</c>, which is the agent, the wrapper and the login
/// command in one binary — one thing for a user to install, put on PATH and update.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Exit codes we own. Anything else came from the child.</summary>
    private const int ExitUsage = 2;
    private const int ExitAgentUnavailable = 3;

    /// <summary>Matches the shell convention for "command could not be run".</summary>
    private const int ExitCannotRun = 127;

    /// <summary>A second agent for the same user, which is always a mistake.</summary>
    private const int ExitAlreadyRunning = 4;

    /// <summary>Nobody is signed in, or the cached sign-in no longer works.</summary>
    private const int ExitNotSignedIn = 5;

    /// <summary>Sign-in was attempted and refused.</summary>
    private const int ExitAuthFailed = 6;

    /// <summary>At least one install or uninstall step did not work.</summary>
    private const int ExitInstallFailed = 7;

    /// <summary>A shortcut could not be wrapped, and the reason has been printed.</summary>
    private const int ExitCannotWrap = 8;

    public static async Task<int> Main(string[] args)
    {
        ParsedCommand command = CommandLine.Parse(args);

        switch (command.Kind)
        {
            case CommandKind.Help:
                if (command.Error is not null)
                {
                    Console.Error.WriteLine($"1remote: {command.Error}");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(CommandLine.Usage);
                    return ExitUsage;
                }

                Console.WriteLine(CommandLine.Usage);
                return 0;

            case CommandKind.Version:
                Console.WriteLine(ProductVersion.Current);
                return 0;

            case CommandKind.Agent:
                // Before anything else, and before a line is printed: if this process
                // was given a console of its own, the agent proper runs in a copy that
                // has none, and this one leaves so its window goes with it.
                if (AgentConsole.HandOffIfOurs(args))
                {
                    return 0;
                }

                return await RunAgentAsync().ConfigureAwait(false);

            case CommandKind.Login:
                return await RunLoginAsync().ConfigureAwait(false);

            case CommandKind.SwitchAccount:
                return await RunSwitchAccountAsync().ConfigureAwait(false);

            case CommandKind.Logout:
                return await RunLogoutAsync().ConfigureAwait(false);

            case CommandKind.Status:
                return await RunStatusAsync().ConfigureAwait(false);

            case CommandKind.Install:
                return RunInstall();

            case CommandKind.Uninstall:
                return RunUninstall();

            case CommandKind.SelfCheck:
                return RunSelfCheck();

            case CommandKind.WrapShortcut:
                return RunWrapShortcut(command);

            default:
                return await RunWrappedAsync(command).ConfigureAwait(false);
        }
    }

    private static Task<int> RunLoginAsync() =>
        SignInAsync(broker => broker.SignInAsync(), "opening your browser to sign in...");

    private static Task<int> RunSwitchAccountAsync() =>
        SignInAsync(
            broker => broker.SwitchAccountAsync(),
            "signing out, then opening your browser to sign in as somebody else...");

    private static async Task<int> SignInAsync(
        Func<TokenBroker, Task<AuthenticationResult>> signIn,
        string announcement)
    {
        var broker = new TokenBroker();

        try
        {
            Console.WriteLine($"1remote: {announcement}");
            AuthenticationResult result = await signIn(broker).ConfigureAwait(false);

            Console.WriteLine(
                $"Signed in as {AccountName.Describe(AccountName.Of(result.ClaimsPrincipal), result.Account.Username)}.");
            Console.WriteLine($"Token valid until {result.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm}.");

            return await ReportHubAdmissionAsync(result.AccessToken).ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            Console.Error.WriteLine($"1remote: sign-in failed ({ex.ErrorCode}): {ex.Message}");
            return ExitAuthFailed;
        }
    }

    /// <summary>
    /// Asks the hub whether it will actually accept this account, because signing in
    /// and being allowed in are two different things.
    /// <para>
    /// Entra will happily issue a valid token to any account the user picks — including
    /// the work account their browser was already signed in to, which on a managed
    /// machine is the likeliest one. That token is genuine and useless: the hub's
    /// allowlist refuses it. Without this check the only symptom is the agent reporting
    /// that the machine is unreachable, minutes later and somewhere else entirely.
    /// </para>
    /// <para>
    /// A hub that cannot be reached is not a sign-in failure, so that case says so and
    /// still exits 0 — someone signing in on a train has done nothing wrong.
    /// </para>
    /// </summary>
    private static async Task<int> ReportHubAdmissionAsync(string accessToken)
    {
        var whoami = new Uri(HubEndpoint.AppUri(), "whoami");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(whoami).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"Could not reach {whoami.Host} to confirm access ({ex.Message.TrimEnd('.')}).");
            return 0;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"{whoami.Host} accepts this account.");
                return 0;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"1remote! {whoami.Host} refused this account, so this machine will not be reachable.");
                Console.Error.WriteLine("1remote! Either sign in as an account on the hub's allowlist, or add this one to it.");
                Console.Error.WriteLine("1remote! Run '1remote logout' then '1remote login' to choose a different account.");
                return ExitAuthFailed;
            }

            Console.WriteLine($"Could not confirm access with {whoami.Host} (HTTP {(int)response.StatusCode}).");
            return 0;
        }
    }

    private static async Task<int> RunLogoutAsync()
    {
        var broker = new TokenBroker();

        Console.WriteLine(await broker.SignOutAsync().ConfigureAwait(false)
            ? "Signed out. Run '1remote login' to sign in again."
            : "Nobody was signed in.");

        return 0;
    }

    private static async Task<int> RunStatusAsync()
    {
        var broker = new TokenBroker();
        AuthStatus status = await broker.GetStatusAsync().ConfigureAwait(false);

        if (!status.IsSignedIn)
        {
            Console.WriteLine("Not signed in. Run '1remote login'.");
            return ExitNotSignedIn;
        }

        Console.WriteLine($"Signed in as {status.Account}.");
        Console.WriteLine($"Token cache: {broker.CachePath}");

        if (status.TokenValidUntil is DateTimeOffset expiry)
        {
            Console.WriteLine($"Access token valid until {expiry.ToLocalTime():yyyy-MM-dd HH:mm}.");
            return 0;
        }

        Console.WriteLine($"The cached sign-in no longer works: {status.Problem}");
        Console.WriteLine("Run '1remote login' to sign in again.");
        return ExitNotSignedIn;
    }

    private static int RunInstall()
    {
        IReadOnlyList<StepResult> steps = Installer.Install(Installer.ExecutablePath, Installer.CurrentUserId);

        return Report(steps, installing: true);
    }

    private static int RunUninstall() => Report(Installer.Uninstall(), installing: false);

    private static int RunSelfCheck()
    {
        IReadOnlyList<StepResult> checks = SelfCheck.Run();

        foreach (StepResult check in checks)
        {
            Console.WriteLine($"  {(check.Ok ? "ok  " : "FAIL")}  {check.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(SelfCheck.Summarise(checks));

        return checks.All(check => check.Ok) ? 0 : ExitInstallFailed;
    }

    /// <summary>
    /// <c>1remote wrap-shortcut</c>: the same work the settings window's button does,
    /// on the command line.
    /// <para>
    /// Both exist deliberately. The window is where somebody finds this; the command is
    /// what a setup script calls, and what the tests drive — none of the interesting
    /// behaviour here is reachable through a file dialog.
    /// </para>
    /// </summary>
    private static int RunWrapShortcut(ParsedCommand command)
    {
        SettingsNotice result = Wrap(command.ShortcutPath!, command.OutputPath);

        if (result.Kind == NoticeKind.Problem)
        {
            Console.Error.WriteLine($"1remote: {result.Text}");
            return ExitCannotWrap;
        }

        Console.WriteLine(result.Text);
        return 0;
    }

    /// <summary>
    /// Reads a shortcut, decides what to do about it, and writes the result.
    /// <para>
    /// Shared by the command and the settings window so that the two cannot come to
    /// disagree about what is refused. The outcome comes back as something to say
    /// rather than as an exit code, because one caller prints it and the other puts it
    /// in a message box.
    /// </para>
    /// </summary>
    private static SettingsNotice Wrap(string sourcePath, string? outputPath)
    {
        string full;

        try
        {
            full = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SettingsNotice($"'{sourcePath}' is not a usable path ({ex.Message}).", NoticeKind.Problem);
        }

        if (!File.Exists(full))
        {
            return new SettingsNotice($"There is no file at '{full}'.", NoticeKind.Problem);
        }

        // Checked before reading rather than after: the shell will happily load a
        // non-shortcut through IPersistFile and hand back an empty target, which would
        // otherwise be reported as "Store or packaged app" and send the user looking in
        // the wrong place entirely.
        if (!string.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new SettingsNotice(
                $"'{Path.GetFileName(full)}' is not a shortcut. Pick the .lnk you double-click, not the program itself.",
                NoticeKind.Problem);
        }

        ShellLinkInfo source;

        try
        {
            source = ShellLink.Read(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            return new SettingsNotice($"Could not read '{Path.GetFileName(full)}': {ex.Message}", NoticeKind.Problem);
        }

        WrapPlan plan = ShortcutWrapper.Plan(full, source, Installer.ExecutablePath, outputPath);

        if (!plan.Ok)
        {
            return new SettingsNotice(plan.Problem!, NoticeKind.Problem);
        }

        try
        {
            ShellLink.Write(plan.OutputPath, plan.Link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            return new SettingsNotice($"Could not write '{plan.OutputPath}': {ex.Message}", NoticeKind.Problem);
        }

        string done =
            $"Wrapped '{plan.DisplayName}'.{Environment.NewLine}{Environment.NewLine}"
            + $"Created {plan.OutputPath}.{Environment.NewLine}"
            + "Start it from there and the session shows up on your phone.";

        return plan.Warning is null
            ? new SettingsNotice(done, NoticeKind.Information)
            : new SettingsNotice(
                $"{done}{Environment.NewLine}{Environment.NewLine}{plan.Warning}",
                NoticeKind.Warning);
    }

    private static int Report(IReadOnlyList<StepResult> steps, bool installing)    {
        foreach (StepResult step in steps)
        {
            Console.WriteLine($"  {(step.Ok ? "ok  " : "FAIL")}  {step.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(Installer.Summarise(steps, installing));

        // Non-zero on any failure, so a setup script can tell. Printing the failure and
        // exiting 0 is how half-installed machines happen.
        return steps.All(step => step.Ok) ? 0 : ExitInstallFailed;
    }

    private static async Task<int> RunAgentAsync()
    {
        using ILoggerFactory loggers = AgentLogging.Create();
        ILogger logger = loggers.CreateLogger("Agent");

        MachineIdentity identity = MachineIdentity.Load(log: Console.Error.WriteLine);

        var broker = new TokenBroker();
        var sessions = new SessionRegistry();
        Uri hubUri = HubEndpoint.Resolve();

        await using var hub = new AgentHubClient(
            hubUri,
            identity,
            sessions,
            async cancellationToken =>
            {
                try
                {
                    return (await broker.AcquireTokenAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
                }
                catch (NotSignedInException)
                {
                    // Not an error here. The agent is expected to be started before
                    // anyone signs in — at boot, for instance — and must wait rather
                    // than exit.
                    return null;
                }
                catch (MsalException ex)
                {
                    logger.TokenRenewalFailed(ex.ErrorCode);
                    return null;
                }
            },
            loggers.CreateLogger("Hub"));

        await using var host = new AgentHost(
            identity,
            sessions,
            hub,
            log: Console.Error.WriteLine,
            awaitingInput: AwaitingInputOptions.Load(log: Console.Error.WriteLine));

        // Signing in and out happen in other processes, so the only way the agent finds
        // out is by watching the file they share. Without this a sign-out is cosmetic:
        // the agent keeps relaying, and the phone keeps listing the machine.
        using var accounts = new SignedInAccountWatcher(
            broker.CachePath,
            _ => broker.GetAccountAsync(),
            async _ => await hub.CredentialsChangedAsync().ConfigureAwait(false),
            loggers.CreateLogger("Auth"));

        await accounts.StartAsync().ConfigureAwait(false);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Handled, so the agent gets to unwind and tell its wrappers goodbye
            // instead of being killed mid-frame.
            e.Cancel = true;
            stopping.Cancel();
        };

        Console.WriteLine($"1remote agent: {identity.DisplayName} ({identity.MachineId})");
        Console.WriteLine($"Relaying through {hubUri}. Press Ctrl+C to stop.");
        Console.WriteLine($"Logging to {FileLogger.DefaultDirectory}.");

        logger.PipeListening(host.PipeName);

        host.Sessions.Changed += () =>
            Console.WriteLine($"1remote agent: {host.Sessions.Count} session(s) attached.");

        using TrayIcon? tray = StartTray(identity, hub, host, accounts, stopping);

        // The hub loop runs alongside the pipe server rather than gating it: a machine
        // with no internet must still be able to run local sessions.
        Task relay = hub.RunAsync(stopping.Token);

        try
        {
            await host.RunAsync(stopping.Token).ConfigureAwait(false);
            await relay.ConfigureAwait(false);
            return 0;
        }
        catch (AgentAlreadyRunningException ex)
        {
            Console.Error.WriteLine($"1remote: {ex.Message}");
            return ExitAlreadyRunning;
        }
    }

    /// <summary>
    /// Puts an icon in the tray, if there is a desktop to put it on.
    /// <para>
    /// Optional by design. The agent's job is to keep sessions reachable, and it must
    /// do that on a machine with no interactive desktop, under a policy that blocks
    /// shell integration, or with a broken shell — none of which is a reason to stop
    /// relaying.
    /// </para>
    /// </summary>
    private static TrayIcon? StartTray(
        MachineIdentity identity,
        AgentHubClient hub,
        AgentHost host,
        SignedInAccountWatcher accounts,
        CancellationTokenSource stopping)
    {
        if (!Environment.UserInteractive)
        {
            return null;
        }

        AgentState State() =>
            hub.IsConnected ? AgentState.Connected
            : hub.IsSignedOut ? AgentState.SignedOut
            : AgentState.Reconnecting;

        // Built fresh every time the window asks, which is once a second while it is
        // open. A snapshot handed over at startup is the version of this that shows a
        // session which ended twenty minutes ago.
        IReadOnlyList<SessionSummary> Sessions() =>
        [
            .. host.Sessions.Snapshot().Select(session => new SessionSummary(
                session.DisplayName,
                session.StartedUtc,
                host.AwaitingInput.IsAwaitingInput(session),
                session.CliType)),
        ];

        var settings = new SettingsActions(
            Read: () => SettingsPresenter.Present(
                State(),
                accounts.Account?.Description,
                Sessions(),
                DateTimeOffset.UtcNow),

            // Asked of Task Scheduler and the registry, never remembered. The user can
            // have turned this off in Task Manager's Startup tab since the agent
            // started, and a tick that lies about it is worse than no tick.
            ReadStartAtLogon: Autostart.IsEnabled,
            WriteStartAtLogon: SetStartAtLogon,

            // Through the CLI rather than in-process, like every other credential
            // action here: the child prints why the hub refused an account, which is
            // the failure people actually hit and one a tray icon cannot explain.
            SignIn: () => Launch(Installer.ExecutablePath, "login"),
            SignOut: () => Launch(Installer.ExecutablePath, "logout"),
            OpenLogs: () => Launch(FileLogger.DefaultDirectory),

            // A mailto: goes to the shell exactly like a URL does, so the user's own
            // mail client opens with the version already in the subject.
            SendFeedback: () => Launch(Feedback.MailTo),
            WrapShortcut: PickAndWrap);

        TrayIcon tray;

        try
        {
            tray = new TrayIcon(
                identity.DisplayName,
                onShowSessions: () => Launch(HubEndpoint.AppUri().ToString()),
                onQuit: stopping.Cancel,
                settings);

            tray.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"1remote: no tray icon ({ex.Message}). The agent is running anyway.");
            return null;
        }

        void Refresh() => tray.Update(State(), host.Sessions.Count, accounts.Account?.Description);

        hub.StateChanged += Refresh;
        host.Sessions.Changed += Refresh;
        accounts.Changed += Refresh;

        Refresh();

        return tray;
    }

    /// <summary>
    /// Turns start-at-logon on or off, and says what went wrong if anything did.
    /// <para>
    /// The same two steps <c>1remote install</c> runs, so a machine set up from the
    /// window and one set up from the command line end up identical. Only the first
    /// failure is reported: the fallback to the Run key means a failed step is often
    /// followed by a successful one, and listing both would read as though nothing had
    /// worked.
    /// </para>
    /// </summary>
    private static string? SetStartAtLogon(bool enable)
    {
        IReadOnlyList<StepResult> steps = enable
            ? Autostart.Enable(Installer.ExecutablePath, Installer.CurrentUserId)
            : Autostart.Disable();

        return Autostart.IsEnabled() == enable
            ? null
            : steps.FirstOrDefault(step => !step.Ok).Message is { Length: > 0 } problem
                ? problem
                : Autostart.Summarise(steps, enable);
    }

    /// <summary>
    /// Asks for a shortcut and wraps it, for the settings window's button.
    /// <para>
    /// Starts in the Desktop folder, because a shortcut somebody launches their CLI
    /// from is on their desktop by definition — that is the entire premise of the
    /// feature (issue #66).
    /// </para>
    /// </summary>
    private static SettingsNotice? PickAndWrap(IntPtr owner)
    {
        string? picked = FilePicker.PickShortcut(
            owner,
            "Choose a shortcut to wrap",
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        // Cancelling is a decision, not a failure. Saying anything about it would make
        // backing out of the dialog cost an extra click.
        return picked is null ? null : Wrap(picked, null);
    }

    /// <summary>Hands something to the shell to open — a URL, a folder or this executable.</summary>
    private static void Launch(string target, string? arguments = null)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(target)
            {
                Arguments = arguments ?? string.Empty,

                // Required for a URL or a folder: without it the target must be an
                // executable, and "https://..." is not.
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"1remote: could not open {target} ({ex.Message}).");
        }
    }

    private static async Task<int> RunWrappedAsync(ParsedCommand command)
    {
        IAgentConnection agent;

        try
        {
            agent = command.RequireAgent
                ? await AgentConnector.ConnectAsync(CancellationToken.None).ConfigureAwait(false)
                : new DetachedAgentConnection();
        }
        catch (AgentUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitAgentUnavailable;
        }

        if (!command.RequireAgent)
        {
            // Loud on purpose. --no-agent is the one path where the product's promise
            // does not hold, so it is never allowed to look like a normal run.
            Console.Error.WriteLine("1remote: running with --no-agent. THIS SESSION IS NOT SHAREABLE.");
        }

        await using (agent)
        {
            // Enter raw mode before the child starts, so its very first frame is
            // already being painted by a terminal that will not reinterpret it.
            using var terminal = WindowsLocalTerminal.Enter();

            PseudoConsoleSession pty;

            try
            {
                pty = PseudoConsoleSession.Start(
                    CommandLine.Encode(command.Program!, command.Args),
                    workingDirectory: null,
                    terminal.Cols,
                    terminal.Rows);
            }
            catch (Win32Exception ex)
            {
                Console.Error.WriteLine($"1remote: could not start '{command.Program}': {ex.Message}");
                return ExitCannotRun;
            }

            await using (pty)
            {
                await agent.OpenSessionAsync(
                    new SessionStartInfo(
                        command.Program!,
                        command.Args,
                        Environment.CurrentDirectory,
                        terminal.Cols,
                        terminal.Rows,
                        command.DisplayName),
                    CancellationToken.None).ConfigureAwait(false);

                var session = new WrapperSession(pty, terminal, agent, Console.Error.WriteLine);
                return await session.RunAsync().ConfigureAwait(false);
            }
        }
    }
}
