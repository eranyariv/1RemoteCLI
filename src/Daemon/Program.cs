using System.ComponentModel;
using System.Reflection;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Cli;
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
                Console.Error.WriteLine("1remote: sign-in is not implemented yet.");
                return ExitCannotRun;

            default:
                return await RunWrappedAsync(command).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAgentAsync()
    {
        MachineIdentity identity = MachineIdentity.Load(log: Console.Error.WriteLine);

        await using var host = new AgentHost(identity, log: Console.Error.WriteLine);

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

        host.Sessions.Changed += () =>
            Console.WriteLine($"1remote agent: {host.Sessions.Count} session(s) attached.");

        try
        {
            await host.RunAsync(stopping.Token).ConfigureAwait(false);
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
