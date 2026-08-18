namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// Watches the two failures that are otherwise only discoverable when somebody
/// complains, and says something when either stops being background noise.
/// <para>
/// A rate rather than an event. One failed push is a phone in a tunnel and one rejected
/// token is a clock that is slightly off — reporting either would train the operator to
/// ignore the channel within a day. A run of them means a push service is down, a
/// subscription set has gone stale, something is misconfigured, or somebody is trying
/// the door, and all four are worth an interruption.
/// </para>
/// <para>
/// Fixed windows rather than a sliding one: the counters reset wholesale every fifteen
/// minutes, so the memory is three integers and there is no per-event bookkeeping on a
/// path that only runs when things are already going wrong. The cost is that a spike
/// straddling a boundary can be reported as two smaller ones, which for an alert whose
/// job is "look at this" changes nothing.
/// </para>
/// </summary>
public sealed class FailureRates(IOperatorNotifier notifier, TimeProvider time)
{
    /// <summary>How long a window lasts before its counters reset.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many failures inside one window are worth a message.
    /// <para>
    /// Ten. Low enough to catch a push service outage while it is still happening, high
    /// enough that a handful of phones rotating their subscriptions overnight — which is
    /// normal and self-healing — stays quiet.
    /// </para>
    /// </summary>
    public const int Threshold = 10;

    private readonly IOperatorNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly object _gate = new();

    private DateTimeOffset _opened;
    private int _pushFailures;
    private int _pushExpired;
    private int _tokenFailures;
    private bool _pushReported;
    private bool _tokenReported;

    /// <summary>A push delivery failed. <paramref name="expired"/> is a 404 or 410 — a dead subscription.</summary>
    public void PushFailed(bool expired)
    {
        OperatorMessage? message = null;

        lock (_gate)
        {
            Roll();

            _pushFailures++;

            if (expired)
            {
                _pushExpired++;
            }

            if (_pushFailures >= Threshold && !_pushReported)
            {
                _pushReported = true;
                message = new OperatorMessage.PushFailuresSpiked(_pushFailures, _pushExpired, (int)Window.TotalMinutes);
            }
        }

        // Sent outside the lock. The notifier only enqueues, but a lock held across a
        // call into another component is how the next deadlock gets written.
        if (message is not null)
        {
            _notifier.Send(message);
        }
    }

    /// <summary>A token was presented and rejected.</summary>
    public void TokenRejected()
    {
        OperatorMessage? message = null;

        lock (_gate)
        {
            Roll();

            _tokenFailures++;

            if (_tokenFailures >= Threshold && !_tokenReported)
            {
                _tokenReported = true;
                message = new OperatorMessage.TokenFailuresSpiked(_tokenFailures, (int)Window.TotalMinutes);
            }
        }

        if (message is not null)
        {
            _notifier.Send(message);
        }
    }

    private void Roll()
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (now - _opened < Window)
        {
            return;
        }

        _opened = now;
        _pushFailures = 0;
        _pushExpired = 0;
        _tokenFailures = 0;
        _pushReported = false;
        _tokenReported = false;
    }
}
