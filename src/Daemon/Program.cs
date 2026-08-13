using System.ComponentModel;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Identity.Client;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Auth;
using OneRemoteCli.Daemon.Cli;
using OneRemoteCli.Daemon.Hub;
using OneRemoteCli.Daemon.Pty;
using OneRemoteCli.Daemon.Wrapper;

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
                Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
                return 0;

            case CommandKind.Agent:
                return await RunAgentAsync().ConfigureAwait(false);

            case CommandKind.Login:
                return await RunLoginAsync().ConfigureAwait(false);

            case CommandKind.Logout:
                return await RunLogoutAsync().ConfigureAwait(false);

            case CommandKind.Status:
                return await RunStatusAsync().ConfigureAwait(false);

            default:
                return await RunWrappedAsync(command).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunLoginAsync()
    {
        var broker = new TokenBroker();

        try
        {
            Console.WriteLine("1remote: opening your browser to sign in...");
            AuthenticationResult result = await broker.SignInAsync().ConfigureAwait(false);

            Console.WriteLine($"Signed in as {result.Account.Username}.");
            Console.WriteLine($"Token valid until {result.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm}.");
            return 0;
        }
        catch (MsalException ex)
        {
            Console.Error.WriteLine($"1remote: sign-in failed ({ex.ErrorCode}): {ex.Message}");
            return ExitAuthFailed;
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

    private static async Task<int> RunAgentAsync()
    {
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
                    Console.Error.WriteLine($"1remote: could not get a token ({ex.ErrorCode}).");
                    return null;
                }
            },
            log: Console.Error.WriteLine);

        await using var host = new AgentHost(identity, sessions, hub, log: Console.Error.WriteLine);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Handled, so the agent gets to unwind and tell its wrappers goodbye
            // instead of being killed mid-frame.
            e.Cancel = true;
            stopping.Cancel();
        };

        Console.WriteLine($"1remote agent: {identity.DisplayName} ({identity.MachineId})");
        Console.WriteLine($"Listening on \\\\.\\pipe\\{host.PipeName}. Press Ctrl+C to stop.");
        Console.WriteLine($"Relaying through {hubUri}.");

        host.Sessions.Changed += () =>
            Console.WriteLine($"1remote agent: {host.Sessions.Count} session(s) attached.");

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
