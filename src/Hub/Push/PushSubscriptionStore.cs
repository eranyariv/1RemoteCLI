using System.Collections.Concurrent;

namespace OneRemoteCli.Hub.Push;

/// <summary>One browser's push subscription, as the browser described it.</summary>
/// <param name="Endpoint">The push service URL. Unique per subscription, so it is the identity.</param>
/// <param name="P256dh">The subscriber's public key, base64url.</param>
/// <param name="Auth">The subscriber's auth secret, base64url.</param>
public readonly record struct PushSubscription(
    string Endpoint,
    string P256dh,
    string Auth,
    PushNotificationKinds DisabledKinds = PushNotificationKinds.None)
{
    public bool Allows(PushNotificationKinds kind) => (DisabledKinds & kind) == 0;
}

/// <summary>
/// Who to push to, per user.
/// <para>
/// In memory, which is the accepted limitation of the whole no-database design: a
/// hub restart drops every subscription until each phone opens the app again and
/// re-registers. It is logged rather than silent, because the symptom - notifications
/// that simply stop - is otherwise indistinguishable from a broken heuristic, a
/// revoked permission, or a phone in a tunnel, and a user cannot tell those apart.
/// If it turns out to be annoying, persisting just this is the natural first
/// exception to the rule.
/// </para>
/// <para>
/// Keyed by user first, then by endpoint. The endpoint is the browser's own identity
/// for the subscription, so re-registering the same phone replaces its entry instead
/// of accumulating duplicates and notifying it twice - which is what happens if you
/// key by connection, since a phone gets a new connection every time it wakes up.
/// </para>
/// </summary>
public sealed class PushSubscriptionStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PushSubscription>> _byUser =
        new(StringComparer.Ordinal);

    public int UserCount => _byUser.Count;

    /// <summary>
    /// Every account with at least one live subscription.
    /// <para>
    /// For <c>/broadcast</c>, which is the one thing in this product that has to reach
    /// everybody rather than one person's own devices. "The hub is down for ten minutes"
    /// is otherwise impossible to say.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Users => [.. _byUser.Keys];

    /// <summary>Adds or replaces a subscription. Returns false if it was already known, unchanged.</summary>
    public bool Register(string userKey, PushSubscription subscription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        ConcurrentDictionary<string, PushSubscription> subscriptions =
            _byUser.GetOrAdd(userKey, _ => new ConcurrentDictionary<string, PushSubscription>(StringComparer.Ordinal));

        bool existed = subscriptions.TryGetValue(subscription.Endpoint, out PushSubscription previous);
        subscriptions[subscription.Endpoint] = subscription;

        return !existed || !previous.Equals(subscription);
    }

    public IReadOnlyList<PushSubscription> For(string userKey) =>
        _byUser.TryGetValue(userKey, out ConcurrentDictionary<string, PushSubscription>? subscriptions)
            ? [.. subscriptions.Values]
            : [];

    /// <summary>
    /// Drops a subscription the push service has told us is dead.
    /// <para>
    /// Called on 404 and 410, which are how a push service reports that the app was
    /// uninstalled or the subscription rotated. Keeping it would mean paying for a
    /// failed request per notification for the life of the process.
    /// </para>
    /// </summary>
    public bool Forget(string userKey, string endpoint)
    {
        if (!_byUser.TryGetValue(userKey, out ConcurrentDictionary<string, PushSubscription>? subscriptions))
        {
            return false;
        }

        bool removed = subscriptions.TryRemove(endpoint, out _);

        // Not left behind as an empty shell: users come and go, and an entry per
        // account that ever registered is a slow leak in a process meant to run for
        // months.
        if (removed && subscriptions.IsEmpty)
        {
            _byUser.TryRemove(userKey, out _);
        }

        return removed;
    }
}
