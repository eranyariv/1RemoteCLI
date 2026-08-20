using System.Threading.Channels;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>Something the agent asked the wrapper to do to its pseudoconsole.</summary>
public abstract record AgentCommand
{
    private AgentCommand()
    {
    }

    /// <summary>Bytes to write into the PTY, uninterpreted, exactly as a keypress would be.</summary>
    public sealed record Input(byte[] Bytes) : AgentCommand;

    /// <summary>The remote viewport changed; reflow the pseudoconsole.</summary>
    public sealed record Resize(int Cols, int Rows) : AgentCommand;

    /// <summary>Send <c>0x03</c>, the Ctrl+C byte.</summary>
    public sealed record Interrupt : AgentCommand;
}

/// <summary>Details the agent needs in order to register a new session.</summary>
public sealed record SessionStartInfo(
    string Program,
    IReadOnlyList<string> Args,
    string Cwd,
    int Cols,
    int Rows,
    string? DisplayName,
    CliType? CliType = null);

/// <summary>
/// The wrapper's link to the agent.
/// <para>
/// An interface so the tee can be tested without a pipe, and so the named-pipe
/// transport can be built independently. The wrapper needs nothing from the agent
/// beyond this: it never talks to the network itself.
/// </para>
/// </summary>
public interface IAgentConnection : IAsyncDisposable
{
    /// <summary>Registers the session and returns the id the agent assigned it.</summary>
    Task<string> OpenSessionAsync(SessionStartInfo info, CancellationToken cancellationToken);

    /// <summary>Forwards raw PTY output. Never blocks the desk terminal.</summary>
    ValueTask SendOutputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Reports that the child exited, so the agent can retire the session.</summary>
    ValueTask CloseSessionAsync(int exitCode, CancellationToken cancellationToken);

    /// <summary>Commands arriving from the phone, via the agent.</summary>
    ChannelReader<AgentCommand> Commands { get; }
}

/// <summary>
/// Thrown when the agent is not reachable. Fatal by design: a session the user
/// believes is shareable but is not is worse than one that refuses to start.
/// </summary>
public sealed class AgentUnavailableException : Exception
{
    public AgentUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
