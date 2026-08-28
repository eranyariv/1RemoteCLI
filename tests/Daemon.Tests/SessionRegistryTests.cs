using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The registry-level half of reconnect support (issue #174): a wrapper that comes
/// back asks for its old session id, and the registry must decide, on its own,
/// whether handing it back is safe.
/// </summary>
public class SessionRegistryTests
{
    [Fact]
    public void GivesAFreshSessionARandomIdWhenNoPriorIdIsRequested()
    {
        var registry = new SessionRegistry();

        TerminalSession session = registry.Add("pwsh", [], @"C:\work", 80, 24, null, new NullChannel());

        Assert.True(Guid.TryParse(session.SessionId, out _));
    }

    /// <summary>
    /// The headline case: an agent that just restarted has an empty registry, so a
    /// reconnecting wrapper's requested id is always free, and reusing it is what
    /// keeps the phone's open tab pointed at the same session across the restart.
    /// </summary>
    [Fact]
    public void ReusesTheRequestedIdWhenItIsFree()
    {
        var registry = new SessionRegistry();

        TerminalSession session = registry.Add(
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            priorSessionId: "session-from-before-the-restart");

        Assert.Equal("session-from-before-the-restart", session.SessionId);
    }

    /// <summary>
    /// Reusing an id must never silently steal one already in use — a second wrapper
    /// racing in with the same requested id must not overwrite the first and leave
    /// its input routed nowhere.
    /// </summary>
    [Fact]
    public void FallsBackToAFreshIdWhenTheRequestedOneIsAlreadyTaken()
    {
        var registry = new SessionRegistry();

        TerminalSession original = registry.Add(
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            priorSessionId: "wanted-id");

        TerminalSession second = registry.Add(
            "cmd.exe",
            [],
            @"C:\elsewhere",
            80,
            24,
            null,
            new NullChannel(),
            priorSessionId: "wanted-id");

        Assert.Equal("wanted-id", original.SessionId);
        Assert.NotEqual("wanted-id", second.SessionId);
        Assert.NotEqual(original.SessionId, second.SessionId);

        // Both sessions must still be independently reachable — the point of falling
        // back is that neither one goes missing.
        Assert.Same(original, registry.Get(original.SessionId));
        Assert.Same(second, registry.Get(second.SessionId));
    }

    /// <summary>
    /// Once the original session ends, its id is free again, and a wrapper that
    /// reconnects afterwards may reuse it — this is the ordinary case of a wrapper
    /// reconnecting within one agent's lifetime, not only across a restart.
    /// </summary>
    [Fact]
    public void ReusesAPriorIdOnceItHasBeenRemoved()
    {
        var registry = new SessionRegistry();

        TerminalSession first = registry.Add(
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            priorSessionId: "reused-id");

        registry.Remove(first.SessionId);

        TerminalSession second = registry.Add(
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            priorSessionId: "reused-id");

        Assert.Equal("reused-id", second.SessionId);
    }

    /// <summary>
    /// Whatever the wrapper sends alongside the id survives the reuse: reusing an id
    /// is not a special, metadata-losing path, it is the same construction with a
    /// chosen id.
    /// </summary>
    [Fact]
    public void PreservesTheSessionsOwnMetadataWhenReusingAnId()
    {
        var registry = new SessionRegistry();

        TerminalSession session = registry.Add(
            "pwsh",
            ["-NoLogo"],
            @"C:\work",
            120,
            30,
            "nightly build",
            new NullChannel(),
            CliType.Generic,
            priorSessionId: "kept-id",
            supportsReconnect: true);

        Assert.Equal("kept-id", session.SessionId);
        Assert.Equal("pwsh", session.Program);
        Assert.Equal(["-NoLogo"], session.Args);
        Assert.Equal(@"C:\work", session.Cwd);
        Assert.Equal(120, session.Cols);
        Assert.Equal(30, session.Rows);
        Assert.Equal("nightly build", session.DisplayName);
        Assert.Equal(CliType.Generic, session.CliType);
        Assert.True(session.ForceSnapshots);
    }

    /// <summary>
    /// The flag that lets the update restart blocker tell reconnect-capable sessions
    /// apart from ones that would be stranded (issue #174). It defaults to false, so
    /// an older wrapper that never sends it is treated as unable to reconnect.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordsWhetherTheWrapperSupportsReconnect(bool supportsReconnect)
    {
        var registry = new SessionRegistry();

        TerminalSession session = registry.Add(
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            supportsReconnect: supportsReconnect);

        Assert.Equal(supportsReconnect, session.SupportsReconnect);
    }

    [Fact]
    public void DefaultsToNotSupportingReconnectForAnOlderWrapper()
    {
        var registry = new SessionRegistry();

        TerminalSession session = registry.Add("pwsh", [], @"C:\work", 80, 24, null, new NullChannel());

        Assert.False(session.SupportsReconnect);
    }

    private sealed class NullChannel : ISessionChannel
    {
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
