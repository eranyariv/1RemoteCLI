using System.Runtime.Versioning;
using System.Threading.Channels;

namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>
/// Stands in for the agent when the user passed <c>--no-agent</c>.
/// <para>
/// Output goes nowhere and no remote input ever arrives, so the session is local
/// only. This exists for development and for diagnosing whether a problem is in the
/// wrapper or in the agent; the wrapper prints a banner whenever it is used, because
/// the whole point of the product is that a session is shareable.
/// </para>
/// </summary>
public sealed class DetachedAgentConnection : IAgentConnection
{
    private readonly Channel<AgentCommand> _commands = Channel.CreateUnbounded<AgentCommand>();

    public DetachedAgentConnection()
    {
        // Nothing will ever be written, so complete immediately: the command pump
        // should finish rather than park on a channel that can never produce.
        _commands.Writer.Complete();
    }

    public ChannelReader<AgentCommand> Commands => _commands.Reader;

    public Task<string> OpenSessionAsync(SessionStartInfo info, CancellationToken cancellationToken) =>
        Task.FromResult("detached");

    public ValueTask SendOutputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask CloseSessionAsync(int exitCode, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Opens the wrapper's link to the agent.
/// <para>
/// The named-pipe transport is built separately; this is the single place the
/// wrapper reaches for it, so wiring it up is a one-line change here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentConnector
{
    /// <summary>What the user sees when the agent is not running.</summary>
    public const string NotRunningMessage = """
        1remote: the agent is not running, so this session could not be shared.

        Start it with:

          1remote agent

        Refusing to continue: a session that looks shareable but is not would be
        worse than no session at all. Pass --no-agent to run locally anyway.
        """;

    public static Task<IAgentConnection> ConnectAsync(CancellationToken cancellationToken) =>
        throw new AgentUnavailableException(NotRunningMessage);
}
