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
    public void ReplacesASessionInPlaceWhenTheAgentCorrectsIt()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        SessionInfo correction = Session("session-1", CliType.ClaudeCode);
        correction.LocalTasks =
        [
            new ChatTaskEntry
            {
                TaskId = "verify",
                Title = "Verify relay",
                Status = "pending",
            },
        ];
        SessionAddress? address = registry.UpdateSession("agent-a", correction);

        Assert.Equal("machine-a", address!.MachineId);

        SessionInfo stored = Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions);
        Assert.Equal(CliType.ClaudeCode, stored.CliType);
        Assert.Equal("verify", Assert.Single(stored.LocalTasks!).TaskId);
    }

    [Fact]
    public void WillNotUpdateASessionOntoTheListThatIsNotOnIt()
    {
        // An update is a correction, never an announcement. A closed session whose
        // update arrives a moment late would otherwise reappear on the user's list
        // with nothing left alive to ever take it off again.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));

        Assert.Null(registry.UpdateSession("agent-a", Session("session-1")));
        Assert.Empty(Assert.Single(registry.ListMachines(Alice)).Sessions);
    }

    [Fact]
    public void KeepsTheWaitingFlagTheHubOwnsWhenTheAgentUpdatesASession()
    {
        // The agent does not track the idle heuristic, so its record says false. Taking
        // it at its word would clear the amber dot the user is looking at.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.MarkAwaitingInput("agent-a", "session-1", awaiting: true);

        registry.UpdateSession("agent-a", Session("session-1", CliType.Cmd));

        SessionInfo stored = Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions);
        Assert.True(stored.AwaitingInput);
        Assert.Equal(CliType.Cmd, stored.CliType);
    }

    /// <summary>
    /// The push notification is most of why the name lives at the hub at all: it is
    /// what a locked phone shows, and "pwsh is waiting" is not what the user is
    /// waiting on.
    /// </summary>
    [Fact]
    public void UsesTheUsersNameForThePushNotification()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        Assert.True(registry.TryRenameSession("client-a", "machine-a", "session-1", "The deploy", out _, out _));

        SessionAddress? address = registry.MarkAwaitingInput("agent-a", "session-1", awaiting: true);

        Assert.Equal("The deploy", address!.SessionName);
    }

    [Fact]
    public void FallsBackToTheAgentNameForThePushNotificationOnceTheUserClearsTheirs()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));

        SessionInfo session = Session("session-1");
        session.DisplayName = "PowerShell";
        registry.AddSession("agent-a", session);

        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryRenameSession("client-a", "machine-a", "session-1", "The deploy", out _, out _);
        registry.TryRenameSession("client-a", "machine-a", "session-1", null, out _, out _);

        SessionAddress? address = registry.MarkAwaitingInput("agent-a", "session-1", awaiting: true);

        Assert.Equal("PowerShell", address!.SessionName);
    }

    /// <summary>
    /// The agent never sends a custom name and must not be able to introduce one.
    /// The hub is the only writer of that field, so a correction from the agent has
    /// to leave it exactly as the hub last set it.
    /// </summary>
    [Fact]
    public void KeepsTheUsersNameWhenTheAgentCorrectsASession()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryRenameSession("client-a", "machine-a", "session-1", "The deploy", out _, out _);
        registry.UpdateSession("agent-a", Session("session-1", CliType.Cmd));

        SessionInfo stored = Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions);
        Assert.Equal("The deploy", stored.CustomName);
        Assert.Equal(CliType.Cmd, stored.CliType);
    }

    /// <summary>
    /// A machine going away clears its sessions, and they come back when the agent
    /// announces them again. The label has to outlive that, or a rename would be
    /// undone by a wifi blip.
    /// </summary>
    [Fact]
    public void KeepsALabelAcrossTheAgentDisconnecting()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryRenameSession("client-a", "machine-a", "session-1", "The deploy", out _, out _);
        registry.TryPinSession("client-a", "machine-a", "session-1", pinned: true, out _, out _);

        registry.Disconnect("agent-a");
        registry.Connect(Alice, "agent-b");
        registry.RegisterMachine(Alice, "agent-b", Request("machine-a"));
        registry.AddSession("agent-b", Session("session-1"));

        SessionInfo stored = Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions);
        Assert.Equal("The deploy", stored.CustomName);
        Assert.True(stored.Pinned);
    }

    [Fact]
    public void ForgetsALabelWhenItsSessionEnds()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryRenameSession("client-a", "machine-a", "session-1", "The deploy", out _, out _);
        registry.RemoveSession("agent-a", "session-1");
        registry.AddSession("agent-a", Session("session-1"));

        Assert.Null(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).CustomName);
    }

    /// <summary>
    /// A label whose session ended while the machine was offline never hears about it.
    /// The cap is what stops those accumulating, and it drops the ones with no live
    /// session first because those are the ones nobody can ever see again.
    /// </summary>
    [Fact]
    public void DoesNotHoardLabelsForSessionsThatQuietlyEnded()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.AddSession("agent-a", Session("keeper"));
        registry.TryRenameSession("client-a", "machine-a", "keeper", "The deploy", out _, out _);

        // Every one of these is renamed and then vanishes without a close, which is
        // exactly what an agent that drops off the network leaves behind.
        for (int i = 0; i < 200; i++)
        {
            registry.AddSession("agent-a", Session($"ghost-{i}"));
            registry.TryRenameSession("client-a", "machine-a", $"ghost-{i}", $"ghost {i}", out _, out _);
            registry.Disconnect("agent-a");
            registry.Connect(Alice, "agent-a");
            registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
            registry.AddSession("agent-a", Session("keeper"));
        }

        Assert.Equal("The deploy", Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).CustomName);
    }

    [Fact]
    public void WillNotRenameASessionInAnotherUsersPartition()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Bob, "client-b");
        registry.RegisterClient(Bob, "client-b");

        Assert.False(registry.TryRenameSession(
            "client-b",
            "machine-a",
            "session-1",
            "mine now",
            out LabelledSession? result,
            out ErrorNotification? error));

        Assert.Null(result);
        Assert.Equal(ErrorCodes.MachineNotFound, error!.Code);
        Assert.Null(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).CustomName);
    }

    [Fact]
    public void MovesASessionToAProject()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        Assert.True(registry.TryMoveSession(
            "client-a", "machine-a", "session-1", "project-1", out LabelledSession? result, out _));

        Assert.Equal("project-1", result!.Session.ProjectId);
        Assert.Equal("project-1", Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    [Fact]
    public void MovesASessionBackToGeneralWithNull()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryMoveSession("client-a", "machine-a", "session-1", "project-1", out _, out _);
        registry.TryMoveSession("client-a", "machine-a", "session-1", null, out _, out _);

        Assert.Null(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    [Fact]
    public void KeepsAProjectAssignmentAcrossTheAgentDisconnecting()
    {
        // Exactly like a rename surviving a reconnect - the label lives at the hub,
        // not on the agent, so a wifi blip must not silently move a session back to
        // General.
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryMoveSession("client-a", "machine-a", "session-1", "project-1", out _, out _);

        registry.Disconnect("agent-a");
        registry.Connect(Alice, "agent-b");
        registry.RegisterMachine(Alice, "agent-b", Request("machine-a"));
        registry.AddSession("agent-b", Session("session-1"));

        Assert.Equal("project-1", Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    [Fact]
    public void WillNotMoveASessionInAnotherUsersPartition()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.Connect(Bob, "client-b");
        registry.RegisterClient(Bob, "client-b");

        Assert.False(registry.TryMoveSession(
            "client-b", "machine-a", "session-1", "project-1", out LabelledSession? result, out ErrorNotification? error));

        Assert.Null(result);
        Assert.Equal(ErrorCodes.MachineNotFound, error!.Code);
    }

    /// <summary>
    /// Deleting a project reassigns every session under it back to General, on every
    /// machine the user owns - the sweep <see cref="RelayRegistry.ClearProjectAssignments"/>
    /// exists for, driven by <c>RelayHub.DeleteProject</c>.
    /// </summary>
    [Fact]
    public void ClearingAProjectMovesEveryAssignedSessionBackToGeneral()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.AddSession("agent-a", Session("session-2"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryMoveSession("client-a", "machine-a", "session-1", "project-1", out _, out _);
        registry.TryMoveSession("client-a", "machine-a", "session-2", "project-2", out _, out _);

        IReadOnlyList<LabelledSession> affected = registry.ClearProjectAssignments(Alice, "project-1");

        Assert.Equal(["session-1"], affected.Select(a => a.Session.SessionId));

        SessionInfo[] sessions = [.. Assert.Single(registry.ListMachines(Alice)).Sessions];
        Assert.Null(sessions.Single(s => s.SessionId == "session-1").ProjectId);
        Assert.Equal("project-2", sessions.Single(s => s.SessionId == "session-2").ProjectId);
    }

    /// <summary>
    /// The sweep must reach machines that are offline right now, or a deleted
    /// project would resurface the instant that agent reconnects and re-announces.
    /// </summary>
    [Fact]
    public void ClearingAProjectSweepsOfflineMachinesToo()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");

        registry.TryMoveSession("client-a", "machine-a", "session-1", "project-1", out _, out _);
        registry.Disconnect("agent-a");

        IReadOnlyList<LabelledSession> affected = registry.ClearProjectAssignments(Alice, "project-1");
        Assert.Empty(affected); // the session is gone from the online list, but the label is still cleared

        registry.Connect(Alice, "agent-b");
        registry.RegisterMachine(Alice, "agent-b", Request("machine-a"));
        registry.AddSession("agent-b", Session("session-1"));

        Assert.Null(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    [Fact]
    public void ClearingAProjectNeverTouchesAnotherUsersSessions()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");
        registry.TryMoveSession("client-a", "machine-a", "session-1", "project-1", out _, out _);

        Assert.Empty(registry.ClearProjectAssignments(Bob, "project-1"));
        Assert.Equal("project-1", Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    /// <summary>
    /// The backstop for the sweep above: a session that reports a project id that no
    /// longer resolves to a real project self-corrects to General instead of staying
    /// stuck pointing at nothing.
    /// </summary>
    [Fact]
    public void CorrectingAStaleProjectClearsItBackToGeneral()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));
        registry.Connect(Alice, "client-a");
        registry.RegisterClient(Alice, "client-a");
        registry.TryMoveSession("client-a", "machine-a", "session-1", "stale-project", out _, out _);

        registry.CorrectStaleProject(Alice, "machine-a", "session-1");

        Assert.Null(Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
    }

    [Fact]
    public void ApplyingAPersistedProjectRestoresItToAReannouncedSession()
    {
        var registry = new RelayRegistry();

        registry.Connect(Alice, "agent-a");
        registry.RegisterMachine(Alice, "agent-a", Request("machine-a"));
        registry.AddSession("agent-a", Session("session-1"));

        registry.ApplyPersistedProject(Alice, "machine-a", "session-1", "project-1");

        Assert.Equal(
            "project-1",
            Assert.Single(Assert.Single(registry.ListMachines(Alice)).Sessions).ProjectId);
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

    private static SessionInfo Session(string sessionId, CliType cliType = CliType.Generic) => new()
    {
        SessionId = sessionId,
        Program = "pwsh",
        Args = [],
        Cwd = @"C:\Projects",
        Cols = 120,
        Rows = 30,
        StartedAt = DateTimeOffset.UnixEpoch,
        CliType = cliType,
    };
}
