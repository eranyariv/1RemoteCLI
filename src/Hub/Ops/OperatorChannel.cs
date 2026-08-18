using System.Threading.Channels;

namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// Accepts a message for the operator and returns immediately.
/// <para>
/// Deliberately <c>void</c>, for exactly the reason <c>IPushNotifier</c> is. Several of
/// these are raised from hub methods and from the authentication handshake, and SignalR
/// processes one invocation at a time per connection — a call site that awaited the Bot
/// API would let Telegram being slow, rate-limited or down stall a user's session. The
/// relay is the half of this product with a person waiting on it. A report to the
/// operator is the half that can be a minute late, or lost.
/// </para>
/// </summary>
public interface IOperatorNotifier
{
    void Send(OperatorMessage message);
}

/// <summary>
/// The queue between the hub and Telegram.
/// <para>
/// Bounded, and full drops the oldest. Unbounded would turn a Bot API outage into hub
/// memory growth; dropping the newest would discard the message that is most likely to
/// matter. Small, because these are events an operator reads, not a stream — if there
/// are two hundred waiting, the situation being reported has already moved on.
/// </para>
/// </summary>
public sealed class OperatorQueue(ILogger<OperatorQueue> logger) : IOperatorNotifier
{
    private const int Capacity = 256;

    private readonly Channel<OperatorMessage> _channel = Channel.CreateBounded<OperatorMessage>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly ILogger<OperatorQueue> _logger = logger;

    public ChannelReader<OperatorMessage> Reader => _channel.Reader;

    public void Send(OperatorMessage message)
    {
        if (message is null)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(message))
        {
            _logger.LogWarning("The operator channel queue rejected a message.");
        }
    }
}

/// <summary>Nothing is configured. Every message is discarded, and the hub said so at startup.</summary>
public sealed class DisabledOperatorNotifier : IOperatorNotifier
{
    public void Send(OperatorMessage message)
    {
    }
}

/// <summary>Drains the queue. One at a time, in order, forever.</summary>
public sealed class OperatorDispatcher(
    OperatorQueue queue,
    IOperatorSender sender,
    TimeProvider time,
    ILogger<OperatorDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// The gap left between messages.
    /// <para>
    /// The Bot API allows roughly one message per second to a chat and answers 429 above
    /// it. Pacing here rather than only reacting to 429 keeps a burst — a restart that
    /// raises several alerts at once — from spending its first few messages learning the
    /// limit.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (OperatorMessage message in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await sender.SendAsync(message.Render(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The loop outlives any one delivery. A dispatcher that died on a
                    // single failed send would take every future report with it, and
                    // would do so silently — which is the exact failure this whole
                    // channel exists to prevent.
                    logger.LogWarning(ex, "An operator message could not be delivered.");
                }

                await Task.Delay(Pace, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
