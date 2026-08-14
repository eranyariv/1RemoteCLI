using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;
using Xunit.Abstractions;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The numbers in section 7 of the design spec, measured against the real thing.
/// <para>
/// A non-functional requirement nobody measures is a wish. These tests take each row
/// of the performance and capacity tables and produce an actual figure for it, through
/// the whole stack: a phone's SignalR connection, the real hub, the real agent, a real
/// named pipe, a real pseudoconsole and a real program running inside it. Every figure
/// is printed, and the printed figures — not the assertions — are the validation
/// record.
/// </para>
/// <para>
/// <b>Why the assertions are looser than the targets.</b> The targets describe a phone
/// talking to a deployed hub. These run on whatever machine happens to have checked the
/// code out, including a shared two-core CI runner that is also compiling something
/// else. Asserting the target exactly would make this the suite's flakiest file and it
/// would be deleted within a month. So each assertion allows <see cref="Slack"/> times
/// the target: enough headroom to survive a noisy runner, tight enough that a change
/// which makes something an order of magnitude worse still fails. The exact figure is
/// in the output for a human to read.
/// </para>
/// <para>
/// Latency here <em>excludes</em> network round trip, exactly as the spec's first row
/// says: everything but the child process is in this process, talking over loopback.
/// </para>
/// </summary>
[Collection("Non-functional")]
[SupportedOSPlatform("windows")]
public sealed class NonFunctionalTests : IAsyncLifetime
{
    /// <summary>
    /// How much worse than the target a measurement may be before the test fails.
    /// </summary>
    private const double Slack = 3.0;

    private readonly ITestOutputHelper _output;
    private EndToEndHarness _harness = null!;

    public NonFunctionalTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync() => _harness = await EndToEndHarness.StartAsync();

    public Task DisposeAsync() => _harness.DisposeAsync().AsTask();

    /// <summary>The scripted CLI, which is the only program that will say when it was typed at.</summary>
    private static string Script => Path.Combine(AppContext.BaseDirectory, "1remote-e2e-script.exe");

    /// <summary>
    /// Row 1 and row 2 of the performance table, measured separately.
    /// <para>
    /// Separating them is the whole reason the scripted CLI stamps a keystroke's arrival.
    /// A round trip timed from outside is input latency plus output latency plus the
    /// program's own think time, and a regression in either leg hides inside the total.
    /// With a stamp taken by the program, send-to-stamp is the input leg and
    /// stamp-to-frame is the output leg, and both processes share one machine clock so
    /// the subtraction means something.
    /// </para>
    /// </summary>
    [Fact]
    public async Task KeystrokeAndOutputLatencyMeetTheirBudgets()
    {
        WrappedShell shell = await _harness.StartShellAsync(Script, "latency");
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        var arrivals = new Arrivals(phone);

        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId));
        await arrivals.WaitForAsync(new Regex("E2E-READY"));

        List<double> input = [];
        List<double> output = [];

        // The first few are thrown away. They pay for JIT, for the first pass through
        // the coalescer and for conhost's own warm-up, none of which a user ever
        // experiences on the hundredth keystroke of a session.
        const int Warmup = 5;
        const int Samples = 40;

        for (int i = 0; i < Warmup + Samples; i++)
        {
            arrivals.Reset();

            DateTime sent = DateTime.UtcNow;
            Assert.Null(await phone.TypeAsync(shell.SessionId, "t"));

            (DateTime landed, Match match) = await arrivals.WaitForAsync(StampPattern);

            var stamped = new DateTime(
                long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                DateTimeKind.Utc);

            if (i < Warmup)
            {
                continue;
            }

            input.Add((stamped - sent).TotalMilliseconds);
            output.Add((landed - stamped).TotalMilliseconds);
        }

        Report("keystroke to program", input);
        Report("program to frame on the phone", output);

        Within("keystroke p50", Percentile(input, 50), 60);
        Within("keystroke p95", Percentile(input, 95), 150);
        Within("output p50", Percentile(output, 50), 200);
        Within("output p95", Percentile(output, 95), 400);
    }

    /// <summary>
    /// Row 3: how long a phone stares at nothing after asking to watch a session.
    /// <para>
    /// Measured by detaching and reattaching rather than by connecting a new phone each
    /// time, because the cost being budgeted is producing and shipping a screen, not
    /// opening a socket.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SnapshotArrivesPromptlyAfterAttaching()
    {
        WrappedShell shell = await _harness.StartShellAsync(Script, "snapshot");
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        var arrivals = new Arrivals(phone);

        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId));
        await arrivals.WaitForAsync(new Regex("E2E-READY"));

        List<double> delays = [];

        for (int i = 0; i < 25; i++)
        {
            Assert.Null(await phone.DetachAsync(shell.SessionId));

            arrivals.Reset();
            DateTime asked = DateTime.UtcNow;

            Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId));

            DateTime painted = await arrivals.WaitForSnapshotAsync();

            if (i > 0)
            {
                delays.Add((painted - asked).TotalMilliseconds);
            }
        }

        Report("attach to first painted screen", delays);
        Within("snapshot p95", Percentile(delays, 95), 500);
    }

    /// <summary>
    /// Row 4: that sustained output is coalesced into frames rather than relayed write
    /// by write.
    /// <para>
    /// The failure this guards against is not slowness, it is a flood. A program that
    /// writes a thousand short lines produces a thousand pipe reads; forwarding each one
    /// as its own frame would put a thousand SignalR messages on a phone's radio to
    /// paint one screen, and the phone would fall behind and stay behind. So what is
    /// asserted is a ceiling on the frame rate, not a floor: the frames must be fewer
    /// than the writes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SustainedOutputIsCoalescedIntoFrames()
    {
        // Two thousand lines as fast as cmd can print them, which is far faster than
        // any budgeted frame rate.
        WrappedShell shell = await _harness.StartShellAsync(
            "cmd.exe /c \"for /l %i in (1,1,2000) do @echo 1remotecli output line %i\"",
            "burst");

        PhoneClient phone = await _harness.ConnectPhoneAsync();

        List<DateTime> stamps = [];
        long bytes = 0;
        object gate = new();

        phone.FrameArrived += frame =>
        {
            lock (gate)
            {
                stamps.Add(DateTime.UtcNow);
                bytes += frame.Data.Length;
            }
        };

        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId));

        await EndToEndHarness.WaitUntilAsync(
            () => phone.Screen.Contains("line 2000", StringComparison.Ordinal),
            "the whole burst to arrive");

        // The tail of the burst can still be in flight when the last line lands.
        await Task.Delay(500);

        DateTime[] taken;
        long total;

        lock (gate)
        {
            taken = [.. stamps];
            total = bytes;
        }

        double seconds = (taken[^1] - taken[0]).TotalSeconds;
        double rate = seconds <= 0 ? taken.Length : taken.Length / seconds;

        _output.WriteLine(
            $"burst: {taken.Length} frames carrying {total} bytes over {seconds:F2}s "
            + $"= {rate:F1} Hz, {total / (double)taken.Length:F0} bytes per frame");

        // 2000 lines of roughly 30 bytes, so the writes numbered in the thousands.
        Assert.True(
            taken.Length < 500,
            $"The burst was relayed as {taken.Length} frames, which is close to per-write.");

        // The spec says about thirty. Twice that is still coalescing; ten times it is not.
        Assert.True(
            rate <= 30 * Slack,
            $"Frames arrived at {rate:F1} Hz, well above the ~30 Hz the spec budgets.");
    }

    /// <summary>
    /// Row 5: that a phone is usable again quickly after the hub it was talking to has
    /// been replaced underneath it.
    /// <para>
    /// A deployment is the ordinary case of this, not a disaster: the hub is a single
    /// instance by design, so every release drops every connection. What is timed is the
    /// full recovery — the agent back on the hub, a phone attached, and a keystroke
    /// reaching the program — because an agent that has reconnected but cannot yet carry
    /// input has not recovered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RecoversFromAHubRestartWithinBudget()
    {
        WrappedShell shell = await _harness.StartShellAsync(Script, "restart");
        PhoneClient before = await _harness.ConnectPhoneAsync();

        var warm = new Arrivals(before);
        Assert.Null(await before.AttachAsync(shell.MachineIdHint, shell.SessionId));
        await warm.WaitForAsync(new Regex("E2E-READY"));

        DateTime down = DateTime.UtcNow;
        await _harness.RestartHubAsync();

        // A phone that was connected to the old hub reconnects on its own in the app;
        // here a new client stands in for that, because what is being timed is the
        // agent's recovery, which is the part with no user behind it.
        PhoneClient after = await _harness.ConnectPhoneAsync();
        var arrivals = new Arrivals(after);

        await EndToEndHarness.WaitUntilAsync(
            async () => (await after.ListMachinesAsync()).Machines.Any(),
            "the agent to republish itself to the new hub");

        Assert.Null(await after.AttachAsync(shell.MachineIdHint, shell.SessionId));
        await arrivals.WaitForAsync(new Regex("Continue\\?"));

        arrivals.Reset();
        Assert.Null(await after.TypeAsync(shell.SessionId, "t"));
        await arrivals.WaitForAsync(StampPattern);

        double seconds = (DateTime.UtcNow - down).TotalSeconds;
        _output.WriteLine($"hub restart to a keystroke reaching the program: {seconds:F2}s");

        Assert.True(
            seconds <= 5 * Slack,
            $"Recovery took {seconds:F1}s against a budget of 5s.");
    }

    /// <summary>
    /// The capacity table: the sessions, the clients per session and the memory each
    /// session costs.
    /// <para>
    /// Twenty real pseudoconsoles with twenty real shells inside them, because the
    /// question is whether the agent holds up under the load it is specified for and a
    /// stand-in session would answer a different question. Memory is measured as the
    /// managed heap this process grows by, which over-counts: it includes the hub, the
    /// wrappers and the test's own bookkeeping, none of which a shipped agent carries.
    /// An over-count that passes is still a pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CarriesTheCapacityTheSpecClaims()
    {
        const int Sessions = 20;

        long before = Settled();

        List<WrappedShell> shells = [];

        for (int i = 0; i < Sessions; i++)
        {
            shells.Add(await _harness.StartShellAsync("cmd.exe", $"capacity {i}"));
        }

        await EndToEndHarness.WaitUntilAsync(
            () => _harness.Sessions.Count == Sessions,
            $"all {Sessions} sessions to reach the agent");

        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await EndToEndHarness.WaitUntilAsync(
            async () => (await phone.ListMachinesAsync()).Machines
                .SelectMany(machine => machine.Sessions).Count() == Sessions,
            $"the hub to list all {Sessions} sessions");

        // Two phones on one session, which is the other capacity row. Both must see the
        // same output, since a second attach that stole the stream from the first would
        // still satisfy a test that only checked one of them.
        WrappedShell watched = shells[0];
        PhoneClient second = await _harness.ConnectPhoneAsync();

        var one = new Arrivals(phone);
        var two = new Arrivals(second);

        Assert.Null(await phone.AttachAsync(watched.MachineIdHint, watched.SessionId));
        Assert.Null(await second.AttachAsync(watched.MachineIdHint, watched.SessionId));

        Assert.Null(await phone.TypeAsync(watched.SessionId, "echo 1remotecli-capacity\r"));

        await one.WaitForAsync(new Regex("1remotecli-capacity"));
        await two.WaitForAsync(new Regex("1remotecli-capacity"));

        long after = Settled();
        double perSession = (after - before) / (double)Sessions / (1024 * 1024);

        _output.WriteLine(
            $"{Sessions} sessions: managed heap {before / 1024 / 1024.0:F1} MB -> "
            + $"{after / 1024 / 1024.0:F1} MB, {perSession:F2} MB per session");

        Assert.True(
            perSession <= 2 * Slack,
            $"Each session cost {perSession:F2} MB against a budget of 2 MB.");
    }

    private static readonly Regex StampPattern = new(@"E2E-TS (\d+)\r?\n", RegexOptions.Compiled);

    /// <summary>The managed heap after collection, so the figure is live objects rather than garbage.</summary>
    private static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private void Report(string what, IReadOnlyList<double> samples) =>
        _output.WriteLine(
            $"{what}: n={samples.Count} p50={Percentile(samples, 50):F1}ms "
            + $"p95={Percentile(samples, 95):F1}ms max={samples.Max():F1}ms");

    private static void Within(string what, double measured, double budget) =>
        Assert.True(
            measured <= budget * Slack,
            $"{what} was {measured:F1}ms against a budget of {budget}ms "
            + $"(allowed {budget * Slack:F0}ms on an unloaded machine).");

    private static double Percentile(IReadOnlyList<double> samples, int percentile)
    {
        double[] sorted = [.. samples.Order()];
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Frames as they land, with the instant each landed.
    /// <para>
    /// Text is accumulated across frames and matched with a pattern that ends in a
    /// newline, because the coalescer is free to split a line across two frames. Timing
    /// against the frame that carried the first half would quietly flatter every
    /// measurement in this file.
    /// </para>
    /// </summary>
    private sealed class Arrivals
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private DateTime _last;
        private bool _snapshot;

        public Arrivals(PhoneClient phone)
        {
            phone.FrameArrived += frame =>
            {
                lock (_gate)
                {
                    _text.Append(Encoding.UTF8.GetString(frame.Data));
                    _last = DateTime.UtcNow;

                    if (frame.Kind == TerminalOutputKind.Snapshot)
                    {
                        _snapshot = true;
                    }
                }
            };
        }

        public void Reset()
        {
            lock (_gate)
            {
                _text.Clear();
                _snapshot = false;
            }
        }

        public async Task<(DateTime Landed, Match Match)> WaitForAsync(Regex pattern)
        {
            DateTime deadline = DateTime.UtcNow + EndToEndHarness.Patience;

            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    Match match = pattern.Match(_text.ToString());

                    if (match.Success)
                    {
                        return (_last, match);
                    }
                }

                await Task.Delay(2).ConfigureAwait(false);
            }

            throw new TimeoutException($"Nothing matching {pattern} arrived within {EndToEndHarness.Patience}.");
        }

        public async Task<DateTime> WaitForSnapshotAsync()
        {
            DateTime deadline = DateTime.UtcNow + EndToEndHarness.Patience;

            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_snapshot)
                    {
                        return _last;
                    }
                }

                await Task.Delay(1).ConfigureAwait(false);
            }

            throw new TimeoutException($"No snapshot arrived within {EndToEndHarness.Patience}.");
        }
    }
}

/// <summary>
/// Measurements run alone. Timing a keystroke while another test is starting a
/// pseudoconsole on the next core measures the other test.
/// </summary>
[CollectionDefinition("Non-functional", DisableParallelization = true)]
public sealed class NonFunctionalCollection
{
}
