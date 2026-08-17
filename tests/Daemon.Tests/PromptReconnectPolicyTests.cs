using Microsoft.AspNetCore.SignalR.Client;
using OneRemoteCli.Daemon.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// How soon the agent looks for a hub that has gone away.
/// <para>
/// This is one of those settings that is invisible until it is expensive. Nothing
/// asserted the schedule at all while it was SignalR's default, and the cost only
/// showed up as an end-to-end test that timed out on a loaded machine and passed on
/// every developer's — which reads like a flaky test rather than like a phone waiting
/// half a minute after each deployment.
/// </para>
/// </summary>
public sealed class PromptReconnectPolicyTests
{
    private static readonly IRetryPolicy Policy = new PromptReconnectPolicy();

    private static TimeSpan Delay(TimeSpan elapsed, long count = 1)
    {
        TimeSpan? delay = Policy.NextRetryDelay(new RetryContext
        {
            ElapsedTime = elapsed,
            PreviousRetryCount = count,
        });

        return Assert.NotNull(delay);
    }

    /// <summary>
    /// The case this exists for: a deployment. Every release drops every connection,
    /// and the replacement is listening again within seconds, so the only thing
    /// standing between the phone and a working machine is how soon somebody looks.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(30)]
    public void LooksAgainWithinASecondWhileTheHubIsProbablyJustRestarting(int elapsedSeconds)
    {
        TimeSpan delay = Delay(TimeSpan.FromSeconds(elapsedSeconds));

        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The budget is 5s from the hub going down to a keystroke reaching the program,
    /// so the schedule has to fit several attempts inside it — one attempt at four
    /// seconds would meet "under a second per retry" and still miss.
    /// </summary>
    [Fact]
    public void FitsSeveralAttemptsInsideTheRecoveryBudget()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        int attempts = 0;

        while (elapsed < TimeSpan.FromSeconds(5))
        {
            elapsed += Delay(elapsed, attempts + 1);
            attempts++;
        }

        Assert.True(attempts >= 5, $"Only {attempts} attempts fit in the 5s budget.");
    }

    /// <summary>
    /// A hub that has been unreachable for a minute is not mid-restart, and asking it
    /// every second is pure cost — the reason the old policy widened at all.
    /// </summary>
    [Fact]
    public void StopsAskingEverySecondOnceItIsAnOutageRatherThanARestart()
    {
        TimeSpan delay = Delay(TimeSpan.FromMinutes(5));

        Assert.InRange(delay, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Never null. Giving up would hand the connection back to the outer loop to
    /// rebuild from scratch, which is slower and buys nothing: an unreachable relay
    /// degrades the product rather than breaking it, so the agent keeps trying.
    /// </summary>
    [Fact]
    public void NeverGivesUpOnTheHub()
    {
        Assert.NotNull(Policy.NextRetryDelay(new RetryContext
        {
            ElapsedTime = TimeSpan.FromHours(9),
            PreviousRetryCount = 10_000,
        }));
    }

    /// <summary>
    /// Every agent that was talking to this hub was disconnected by the same event and
    /// is now counting the same seconds. In lockstep they would all arrive at the
    /// moment the hub is least able to serve them, having just started.
    /// </summary>
    [Fact]
    public void SpreadsTheHerdRatherThanRetryingInLockstep()
    {
        var delays = new HashSet<TimeSpan>();

        for (int i = 0; i < 50; i++)
        {
            delays.Add(Delay(TimeSpan.FromSeconds(2)));
        }

        Assert.True(delays.Count > 1, "Every agent would retry at exactly the same moment.");
    }
}
