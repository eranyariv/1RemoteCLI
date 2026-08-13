namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Where a session's traffic goes once the agent has it.
/// <para>
/// This is the seam the VT emulator (task 2.5) and the hub client (task 1.8) plug
/// into. The agent skeleton keeps it deliberately empty so that the plumbing can be
/// finished and tested before either of those exists.
/// </para>
/// </summary>
public interface ISessionSink
{
    ValueTask OnOpenedAsync(TerminalSession session, CancellationToken cancellationToken = default);

    ValueTask OnOutputAsync(
        TerminalSession session,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    ValueTask OnClosedAsync(TerminalSession session, int exitCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// The session looks like it is waiting for the user to answer something.
    /// <para>
    /// A guess, not a fact - see <see cref="AwaitingInputHeuristic"/> - and given a
    /// default implementation so that a sink which has no way to reach the user is not
    /// obliged to pretend otherwise.
    /// </para>
    /// </summary>
    /// <param name="hint">The line that appears to be the question, if there is one.</param>
    ValueTask OnAwaitingInputAsync(
        TerminalSession session,
        string? hint,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Drops everything. The default until a real consumer is wired in.</summary>
public sealed class NullSessionSink : ISessionSink
{
    public static readonly NullSessionSink Instance = new();

    public ValueTask OnOpenedAsync(TerminalSession session, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask OnOutputAsync(
        TerminalSession session,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask OnClosedAsync(
        TerminalSession session,
        int exitCode,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
