using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The backpressure rules (spec §4.4, task 3.3).
/// <para>
/// These tests are about one question: what happens to everyone else when one phone
/// stops reading. The answer has to be "nothing", because the alternative — which is
/// what the hub did before this class existed — is that a single phone on a bad link
/// silently freezes every session on the machine, and the user has no way to tell that
/// is what happened.
/// </para>
/// </summary>
public sealed class OutboundFanoutTests
{
    private const string Agent = "agent-1";
    private const string Fast = "fast-client";
    private const string Slow = "slow-client";
    private const string Session = "session-1";

    /// <summary>
    /// A client that never finishes reading does not delay a client that does.
    /// <para>
    /// The reason this component exists. SignalR runs one invocation at a time per
    /// connection, so when the fan-out was awaited inside the agent's invocation, the
    /// slowest reader set the pace for the whole machine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASlowClientDoesNotDelayAFastOne()
    {
        FakeHubContext hub = new();
        hub.Stall(Slow);

        OutboundFanout fanout = Create(hub);

        for (int i = 0; i < 10; i++)
        {
            fanout.Publish(Agent, Frame(i + 1, $"line {i}"), [Slow, Fast]);
        }

        await WaitFor(() => hub.Sent(Fast).Count == 10);

        Assert.Equal(10, hub.Sent(Fast).Count);
        Assert.Single(hub.Sent(Slow));
    }

    /// <summary>Frames reach a client in the order they were published.</summary>
    [Fact]
    public async Task FramesArriveInOrder()
    {
        FakeHubContext hub = new();
        OutboundFanout fanout = Create(hub);

        for (int i = 0; i < 50; i++)
        {
            fanout.Publish(Agent, Frame(i + 1, $"{i}"), [Fast]);
        }

        await WaitFor(() => hub.Sent(Fast).Count == 50);

        Assert.Equal(
            Enumerable.Range(1, 50).Select(i => (long)i),
            hub.Sent(Fast).Select(f => f.Seq));
    }

    /// <summary>
    /// A backlog past the byte limit is thrown away and the agent is asked to repaint
    /// that client.
    /// <para>
    /// Discarding is only safe because of the screen-state model: a snapshot contains
    /// everything the discarded frames would have drawn. Without the repaint request
    /// this would be plain data loss, so the two halves have to be asserted together.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ABacklogPastTheByteLimitIsTradedForARepaint()
    {
        FakeHubContext hub = new();
        hub.Stall(Slow);

        OutboundFanout fanout = Create(hub, new OutboundLimits { MaxQueuedBytes = 8 * 1024 });

        // The first frame is taken by the pump and stalls there; the rest pile up.
        for (int i = 0; i < 20; i++)
        {
            fanout.Publish(Agent, Frame(i + 1, new string('x', 1024)), [Slow]);
        }

        await WaitFor(() => hub.SentRaw(Agent).Count == 1);

        AttachRequestedNotification repaint = Assert.IsType<AttachRequestedNotification>(
            hub.SentRaw(Agent).Single().Argument);

        Assert.Equal(Session, repaint.SessionId);
        Assert.Equal(Slow, repaint.ClientConnectionId);

        // No last sequence forces the agent down its snapshot path, and zero geometry
        // keeps it from reshaping a pseudoconsole nobody asked it to reshape.
        Assert.Null(repaint.LastSeq);
        Assert.Equal(0, repaint.Cols);
        Assert.Equal(0, repaint.Rows);
        Assert.True(repaint.ContinuityLost);

        Assert.Equal(1, fanout.Repaints);
    }

    /// <summary>
    /// A backlog that is small but stale is thrown away too.
    /// <para>
    /// A trickle over a slow link never reaches the byte limit, so on size alone a
    /// client could sit minutes behind and still be "fine". Terminal output that is a
    /// minute old is not a view of anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AStaleBacklogIsTradedForARepaintEvenWhenItIsSmall()
    {
        FakeHubContext hub = new();
        hub.Stall(Slow);

        ManualTime time = new();
        OutboundFanout fanout = Create(
            hub,
            new OutboundLimits { MaxQueuedAge = TimeSpan.FromSeconds(2) },
            time);

        fanout.Publish(Agent, Frame(1, "taken by the pump"), [Slow]);
        await WaitFor(() => hub.Sent(Slow).Count == 1);

        fanout.Publish(Agent, Frame(2, "queued"), [Slow]);
        Assert.Equal(0, fanout.Repaints);

        time.Advance(TimeSpan.FromSeconds(3));
        fanout.Publish(Agent, Frame(3, "and now it is old"), [Slow]);

        await WaitFor(() => fanout.Repaints == 1);
    }

    /// <summary>
    /// Repaints are throttled.
    /// <para>
    /// A link too slow to carry the output is also too slow to carry a screen, so
    /// without a floor the hub would answer every overflow with a snapshot, overflow on
    /// the snapshot, and spend the entire link on repaints that never land.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RepaintsAreNotRequestedMoreOftenThanTheMinimumInterval()
    {
        FakeHubContext hub = new();
        hub.Stall(Slow);

        ManualTime time = new();
        OutboundFanout fanout = Create(
            hub,
            new OutboundLimits
            {
                MaxQueuedBytes = 4 * 1024,
                MinimumRepaintInterval = TimeSpan.FromSeconds(5),
            },
            time);

        for (int i = 0; i < 40; i++)
        {
            fanout.Publish(Agent, Frame(i + 1, new string('x', 1024)), [Slow]);
        }

        await WaitFor(() => fanout.Repaints == 1);

        // Still overflowing, still inside the cooldown.
        for (int i = 0; i < 40; i++)
        {
            fanout.Publish(Agent, Frame(100 + i, new string('x', 1024)), [Slow]);
        }

        Assert.Equal(1, fanout.Repaints);

        time.Advance(TimeSpan.FromSeconds(6));

        for (int i = 0; i < 40; i++)
        {
            fanout.Publish(Agent, Frame(200 + i, new string('x', 1024)), [Slow]);
        }

        await WaitFor(() => fanout.Repaints == 2);
    }

    /// <summary>
    /// A targeted frame reaches only its target.
    /// <para>
    /// This is issue #42. Several phones can watch one session, so a repaint or a
    /// resume replay — which is an answer to one phone's attach, not news about the
    /// session — must not be drawn on the others. A replay is the damaging case: the
    /// other phones already have those bytes, and receiving them a second time writes
    /// them onto the screen again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATargetedFrameReachesOnlyItsTarget()
    {
        FakeHubContext hub = new();
        OutboundFanout fanout = Create(hub);

        fanout.Publish(Agent, Frame(1, "shared"), [Fast, Slow]);

        TerminalOutputNotification targeted = Frame(1, "just for you");
        targeted.TargetConnectionId = Slow;
        fanout.Publish(Agent, targeted, [Fast, Slow]);

        await WaitFor(() => hub.Sent(Slow).Count == 2);
        await WaitFor(() => hub.Sent(Fast).Count == 1);

        Assert.Single(hub.Sent(Fast));
        Assert.Equal(2, hub.Sent(Slow).Count);
    }

    /// <summary>
    /// A targeted frame for a client that is no longer watching is dropped.
    /// <para>
    /// The agent is answering an attach that the hub has since forgotten. Sending it to
    /// the remaining watchers instead would be strictly worse than sending it nowhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATargetedFrameForANonWatcherGoesNowhere()
    {
        FakeHubContext hub = new();
        OutboundFanout fanout = Create(hub);

        TerminalOutputNotification targeted = Frame(1, "for someone who left");
        targeted.TargetConnectionId = "gone";
        fanout.Publish(Agent, targeted, [Fast]);

        fanout.Publish(Agent, Frame(2, "shared"), [Fast]);
        await WaitFor(() => hub.Sent(Fast).Count == 1);

        Assert.Equal(2, hub.Sent(Fast).Single().Seq);
    }

    /// <summary>A forgotten client is sent nothing more.</summary>
    [Fact]
    public async Task AForgottenClientIsSentNothingMore()
    {
        FakeHubContext hub = new();
        OutboundFanout fanout = Create(hub);

        fanout.Publish(Agent, Frame(1, "before"), [Fast]);
        await WaitFor(() => hub.Sent(Fast).Count == 1);

        fanout.Forget(Fast);
        fanout.Publish(Agent, Frame(2, "after"), []);

        await Task.Delay(50);
        Assert.Single(hub.Sent(Fast));
    }

    /// <summary>
    /// Resetting drops the backlog but keeps delivering.
    /// <para>
    /// What attach does. The queued frames belong to a view that is about to be
    /// replaced by a snapshot; the client itself is very much still there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResettingDropsTheBacklogButKeepsDelivering()
    {
        FakeHubContext hub = new();
        hub.Stall(Slow);

        OutboundFanout fanout = Create(hub);

        for (int i = 0; i < 10; i++)
        {
            fanout.Publish(Agent, Frame(i + 1, "stale"), [Slow]);
        }

        await WaitFor(() => hub.Sent(Slow).Count == 1);

        fanout.Reset(Slow);
        hub.Release(Slow);

        fanout.Publish(Agent, Frame(99, "fresh"), [Slow]);

        await WaitFor(() => hub.Sent(Slow).Any(f => f.Seq == 99));

        // The nine frames that were waiting behind the stall are gone, not delivered
        // late on top of the snapshot that replaced them.
        Assert.DoesNotContain(hub.Sent(Slow), f => f.Seq is > 1 and < 99);
    }

    private static OutboundFanout Create(
        FakeHubContext hub,
        OutboundLimits? limits = null,
        TimeProvider? time = null) =>
        new(hub, NullLogger<OutboundFanout>.Instance, limits, time);

    private static TerminalOutputNotification Frame(long seq, string text) => new()
    {
        SessionId = Session,
        Seq = seq,
        Kind = TerminalOutputKind.Delta,
        Data = System.Text.Encoding.UTF8.GetBytes(text),
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the expected state.");
    }

    /// <summary>A clock the test moves by hand, so ageing rules do not need real waiting.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Recorded(string Method, object? Argument);

    /// <summary>
    /// Just enough of SignalR to see what was sent, and to hold a send open.
    /// </summary>
    private sealed class FakeHubContext : IHubContext<RelayHub>
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<Recorded>> _sent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _stalls = new(StringComparer.Ordinal);

        public IHubClients Clients { get; }

        public IGroupManager Groups => throw new NotSupportedException();

        public FakeHubContext() => Clients = new FakeClients(this);

        public void Stall(string connectionId)
        {
            lock (_gate)
            {
                _stalls[connectionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release(string connectionId)
        {
            TaskCompletionSource? stall;

            lock (_gate)
            {
                _stalls.Remove(connectionId, out stall);
            }

            stall?.SetResult();
        }

        public IReadOnlyList<TerminalOutputNotification> Sent(string connectionId) =>
            SentRaw(connectionId)
                .Where(r => r.Argument is TerminalOutputNotification)
                .Select(r => (TerminalOutputNotification)r.Argument!)
                .ToList();

        public IReadOnlyList<Recorded> SentRaw(string connectionId)
        {
            lock (_gate)
            {
                return _sent.TryGetValue(connectionId, out List<Recorded>? list)
                    ? [.. list]
                    : [];
            }
        }

        private Task RecordAsync(string connectionId, string method, object? argument)
        {
            TaskCompletionSource? stall;

            lock (_gate)
            {
                if (!_sent.TryGetValue(connectionId, out List<Recorded>? list))
                {
                    list = [];
                    _sent[connectionId] = list;
                }

                list.Add(new Recorded(method, argument));
                _stalls.TryGetValue(connectionId, out stall);
            }

            return stall?.Task ?? Task.CompletedTask;
        }

        private sealed class FakeClients(FakeHubContext owner) : IHubClients
        {
            public IClientProxy All => throw new NotSupportedException();

            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
                throw new NotSupportedException();

            public IClientProxy Client(string connectionId) => new FakeProxy(owner, connectionId);

            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

            public IClientProxy Group(string groupName) => throw new NotSupportedException();

            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
                throw new NotSupportedException();

            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

            public IClientProxy User(string userId) => throw new NotSupportedException();

            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class FakeProxy(FakeHubContext owner, string connectionId) : IClientProxy
        {
            public Task SendCoreAsync(
                string method,
                object?[] args,
                CancellationToken cancellationToken = default) =>
                owner.RecordAsync(connectionId, method, args.Length > 0 ? args[0] : null);
        }
    }
}
