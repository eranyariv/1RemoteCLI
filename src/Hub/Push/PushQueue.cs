using System.Threading.Channels;

namespace OneRemoteCli.Hub.Push;

/// <summary>
/// Accepts a notification to be delivered and returns immediately.
/// <para>
/// Deliberately <c>void</c>. SignalR processes one invocation at a time per
/// connection, so a hub method that awaited a push service would let a slow or
/// unreachable third party stall every session on the agent that reported the event.
/// The relay is the half of this product with a person waiting on it; notifications
/// are the half that can be a second late.
/// </para>
/// </summary>
public interface IPushNotifier
{
    void Enqueue(string userKey, PushPayload payload);
}

/// <summary>A queued delivery.</summary>
public readonly record struct PushJob(string UserKey, PushPayload Payload);

/// <summary>
/// The queue between the hub and the push services.
/// <para>
/// Bounded, and full means the oldest waiting notification is dropped. An unbounded
/// queue turns a push service outage into hub memory growth; dropping the newest
/// would mean the notification the user most needs is the one discarded. Neither is
/// pleasant, but a stale notification is the cheapest thing in the system to lose.
/// </para>
/// </summary>
public sealed class PushQueue(ILogger<PushQueue> logger) : IPushNotifier
{
    private const int Capacity = 512;

    private readonly Channel<PushJob> _channel = Channel.CreateBounded<PushJob>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly ILogger<PushQueue> _logger = logger;

    public ChannelReader<PushJob> Reader => _channel.Reader;

    public void Enqueue(string userKey, PushPayload payload)
    {
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return;
        }

        if (!_channel.Writer.TryWrite(new PushJob(userKey, payload)))
        {
            _logger.LogWarning("Push queue rejected a notification.");
        }
    }
}

/// <summary>Drains the queue. One at a time, in order, forever.</summary>
public sealed class PushDispatcher(
    PushQueue queue,
    IPushSender sender,
    ILogger<PushDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (PushJob job in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await sender.SendAsync(job.UserKey, job.Payload, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The loop outlives any one delivery. A dispatcher that died on a
                    // single bad subscription would take every future notification with
                    // it, and would do so silently.
                    logger.LogWarning(ex, "Push dispatch failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
