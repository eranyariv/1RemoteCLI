using Microsoft.AspNetCore.SignalR.Client;

namespace OneRemoteCli.Daemon.Hub;

/// <summary>
/// When to try the hub again after the connection drops.
/// <para>
/// SignalR's default policy waits 0s, 2s, 10s, then 30s, and gives up after that. It
/// is built for an arbitrary internet outage, where trying hard early is wasted
/// effort. The outage this agent actually sees is the opposite: the hub is one
/// instance by design, so every deployment drops every connection and the replacement
/// is listening again within seconds.
/// </para>
/// <para>
/// Against that, the default is close to the worst possible schedule. Miss the 2s
/// attempt — which a hub taking three seconds to warm up does routinely — and the
/// next look is not for another eight seconds, and the one after that not for twenty
/// more. Nothing is wrong by then except that nobody is looking, and the phone shows
/// the machine offline for the whole of it. The recovery budget is 5s.
/// </para>
/// <para>
/// So: look roughly every second while it is worth looking, then widen, because a hub
/// that has been dead for a minute is probably dead for a while and polling it is
/// pure cost. Never give up — <see cref="AgentHubClient.RunAsync"/> would only rebuild
/// the connection and start over, and an unreachable relay is a degraded product
/// rather than a broken one.
/// </para>
/// </summary>
internal sealed class PromptReconnectPolicy : IRetryPolicy
{
    /// <summary>How long the hub is given to come back before this stops being a restart and starts being an outage.</summary>
    private static readonly TimeSpan Attentive = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Eager = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(15);

    public TimeSpan? NextRetryDelay(RetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        TimeSpan baseDelay = context.ElapsedTime < Attentive ? Eager : Patient;

        // Jitter, because every agent that was talking to this hub was disconnected by
        // the same event and is now counting the same seconds. Without it they arrive
        // in lockstep, and the moment they all pick is the moment the hub is least able
        // to serve them: it has just started. Full jitter over the interval spreads the
        // herd without making any single agent noticeably slower.
        double spread = Random.Shared.NextDouble();

        return baseDelay * (0.5 + (spread * 0.5));
    }
}
