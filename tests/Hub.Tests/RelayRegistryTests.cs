using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The routing table on its own. The end-to-end behaviour is covered in
/// <see cref="RelayHubTests"/>; these tests pin the rules that are easiest to break
/// by accident later — partition isolation, what survives a disconnect, and the
/// difference between the three ways a target can be absent.
/// </summary>
public sealed class RelayRegistryTests
{
    private const string Alice = "tenant-a:alice";
    private const string Bob = "tenant-b:bob";

    [Fact]
    public void ListsOnlyTheCallersOwnMachines()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a", "Alice's desktop"));

        registry.Connect(Bob, "agent-b");
        registry.RegisterMachine(Bob, "agent-b", Request("machine-b", "Bob's laptop"));

        Assert.Equal(["machine-a"], registry.ListMachines(Alice).Select(m => m.MachineId));
        Assert.Equal(["machine-b"], registry.ListMachines(Bob).Select(m => m.MachineId));
    }

    [Fact]
    public void RefusesAMachineIdThatBelongsToSomebodyElse()
    {
        // The whole point of partitioning: Bob knows Alice's machine id, and it still
        // does not exist as far as he is concerned.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Bob, "client-b");
        registry.RegisterClient(Bob, "client-b");

        bool attached = registry.TryAttach("client-b", "machine-a", "session-1", out _, out ErrorNotification? error);

        Assert.False(attached);
        Assert.Equal(ErrorCodes.MachineNotFound, error!.Code);
    }

    [Fact]
    public void SeparatesMissingMachineFromOfflineMachineFromMissingSession()
    {
        // Three different repairs: check the id, start the agent, start the program.
        // Collapsing them into one error would make every one of those a guess.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryAttach("client-a", "machine-z", "session-1", out _, out ErrorNotification? missingMachine);
        Assert.Equal(ErrorCodes.MachineNotFound, missingMachine!.Code);

        registry.TryAttach("client-a", "machine-a", "session-z", out _, out ErrorNotification? missingSession);
        Assert.Equal(ErrorCodes.SessionNotFound, missingSession!.Code);

        registry.Disconnect("agent-a");

        registry.TryAttach("client-a", "machine-a", "session-1", out _, out ErrorNotification? offline);
        Assert.Equal(ErrorCodes.MachineOffline, offline!.Code);
    }

    [Fact]
    public void KeepsAMachineVisibleButOfflineAfterItsAgentDrops()
    {
        // Vanishing would be worse: the user would not know whether they had the wrong
        // machine or a dead agent.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        DisconnectResult result = registry.Disconnect("agent-a");

        Assert.Equal("machine-a", result.MachineId);
        Assert.Equal(Alice, result.UserKey);

        MachineInfo machine = Assert.Single(registry.ListMachines(Alice));
        Assert.False(machine.Online);
        Assert.Empty(machine.Sessions);
    }

    [Fact]
    public void ForgetsSessionsWhenTheAgentDrops()
    {
        // A session cannot outlive the wrapper hosting it, so keeping it listed would
        // offer the user something that is guaranteed to fail.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Disconnect("agent-a");

        registry.Connect(Alice, "agent-a2");
        registry.RegisterMachine(Alice, "agent-a2", Request("machine-a"));

        Assert.Empty(Assert.Single(registry.ListMachines(Alice)).Sessions);
    }

    [Fact]
    public void LetsAReconnectingAgentTakeOverItsOwnMachine()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-old");
        registry.RegisterMachine(Alice, "agent-old", Request("machine-a"));

        registry.Connect(Alice, "agent-new");
        registry.RegisterMachine(Alice, "agent-new", Request("machine-a"));
        registry.AddSession("agent-new", Session("session-1"));

        Assert.True(Assert.Single(registry.ListMachines(Alice)).Online);

        // The stale connection's eventual disconnect must not knock the live one out.
        DisconnectResult stale = registry.Disconnect("agent-old");

        Assert.Null(stale.MachineId);
        Assert.True(Assert.Single(registry.ListMachines(Alice)).Online);
    }

    [Fact]
    public void IgnoresSessionsFromAConnectionThatNeverRegisteredAMachine()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");

        Assert.Null(registry.AddSession("agent-a", Session("session-1")));
    }

    [Fact]
    public void AttachingToASecondSessionDetachesTheFirst()
    {
        // Otherwise the old agent keeps streaming into nothing.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.AddSession("agent-a", Session("session-2"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryAttach("client-a", "machine-a", "session-1", out _, out _);
        registry.TryAttach("client-a", "machine-a", "session-2", out AttachResult? second, out _);

        Assert.Equal("session-1", second!.Displaced!.SessionId);
        Assert.Equal("agent-a", second.Displaced.AgentConnectionId);

        Assert.Empty(registry.ClientsAttachedTo(Alice, "machine-a", "session-1"));
        Assert.Equal(["client-a"], registry.ClientsAttachedTo(Alice, "machine-a", "session-2"));
    }

    [Fact]
    public void ReattachingToTheSameSessionDisplacesNothing()
    {
        // A reconnecting phone re-attaches to what it was already watching. Telling
        // the agent to detach it would race with the attach that follows.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryAttach("client-a", "machine-a", "session-1", out _, out _);
        registry.TryAttach("client-a", "machine-a", "session-1", out AttachResult? again, out _);

        Assert.Null(again!.Displaced);
        Assert.Equal(["client-a"], registry.ClientsAttachedTo(Alice, "machine-a", "session-1"));
    }

    [Fact]
    public void RefusesToDriveASessionTheClientIsNotWatching()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        bool resolved = registry.TryResolveAttached("client-a", "session-1", out _, out ErrorNotification? error);

        Assert.False(resolved);
        Assert.Equal(ErrorCodes.NotAttached, error!.Code);
        Assert.Equal("session-1", error.SessionId);
    }

    [Fact]
    public void ResolvesTheAgentForAnAttachedClient()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");
        registry.TryAttach("client-a", "machine-a", "session-1", out _, out _);

        Assert.True(registry.TryResolveAttached("client-a", "session-1", out RelayTarget? target, out _));
        Assert.Equal("agent-a", target!.AgentConnectionId);
        Assert.Equal("machine-a", target.MachineId);
    }

    [Fact]
    public void RefusesAClientThatNeverHandshook()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");

        Assert.False(registry.TryAttach("client-a", "machine-a", "session-1", out _, out ErrorNotification? error));
        Assert.Equal(ErrorCodes.InvalidRequest, error!.Code);
    }

    [Fact]
    public void DetachesEveryWatcherWhenASessionEnds()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");
        registry.TryAttach("client-a", "machine-a", "session-1", out _, out _);

        SessionAddress? closed = registry.RemoveSession("agent-a", "session-1");

        Assert.Equal("machine-a", closed!.MachineId);
        Assert.Empty(registry.ClientsAttachedTo(Alice, "machine-a", "session-1"));
    }

    [Fact]
    public void ReportsWhatADisconnectingClientWasWatching()
    {
        // The hub uses this to send DetachRequested on behalf of a phone that lost
        // signal without ever saying goodbye.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");
        registry.TryAttach("client-a", "machine-a", "session-1", out _, out _);

        DisconnectResult result = registry.Disconnect("client-a");

        Assert.Equal("session-1", result.ClientAttachment!.SessionId);
        Assert.Equal("agent-a", result.ClientAttachment.AgentConnectionId);
        Assert.Empty(registry.ClientsOf(Alice));
    }

    [Fact]
    public void FansOutOnlyToTheClientsOfTheOwningUser()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "client-a1");
        registry.RegisterClient(Alice, "client-a1");
        registry.Connect(Alice, "client-a2");
        registry.RegisterClient(Alice, "client-a2");
        registry.Connect(Bob, "client-b");
        registry.RegisterClient(Bob, "client-b");

        Assert.Equal(2, registry.ClientsOf(Alice).Count);
        Assert.Equal(["client-b"], registry.ClientsOf(Bob));
    }

    [Fact]
    public void RemembersThatASessionIsWaitingOnTheUser()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.MarkAwaitingInput("agent-a", "session-1", awaiting: true);
        Assert.True(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).AwaitingInput);

        registry.MarkAwaitingInput("agent-a", "session-1", awaiting: false);
        Assert.False(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).AwaitingInput);
    }

    [Fact]
    public void IgnoresAnUnknownConnection()
    {
        var registry = new RelayRegistry();

        Assert.Same(DisconnectResult.Nothing, registry.Disconnect("never-seen"));
        Assert.Empty(registry.ListMachines(Alice));
    }

    private static RegisterMachineRequest Request(string machineId, string displayName = "Machine") => new()
    {
        MachineId = machineId,
        DisplayName = displayName,
        Os = "Windows",
        AgentVersion = "1.0.0",
        ProtocolVersion = ProtocolVersion.Current,
    };

    private static SessionInfo Session(string sessionId) => new()
    {
        SessionId = sessionId,
        Program = "pwsh",
        Args = [],
        Cwd = @"C:\Projects",
        Cols = 120,
        Rows = 30,
        StartedAt = DateTimeOffset.UnixEpoch,
    };
}
