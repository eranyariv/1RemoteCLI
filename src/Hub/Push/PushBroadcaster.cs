namespace OneRemoteCli.Hub.Push;

/// <summary>
/// Pushes one message to every subscribed phone.
/// <para>
/// An interface of its own, taking a plain string, so the operator channel can broadcast
/// without <see cref="PushPayload"/> ever appearing in its signatures. That matters: the
/// payload's other factories take a machine name and a session name, and a type that can
/// carry those must not be reachable from the code that formats messages for the
/// operator — who is a different person from the user whose sessions they are.
/// </para>
/// </summary>
public interface IPushBroadcaster
{
    /// <summary>Queues the message for every account with a subscription, and returns how many.</summary>
    int Broadcast(string text);
}

/// <summary>Fans a broadcast out over the ordinary push queue, so it obeys the same backpressure.</summary>
public sealed class PushBroadcaster(PushSubscriptionStore store, IPushNotifier notifier) : IPushBroadcaster
{
    public int Broadcast(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        IReadOnlyList<string> users = store.Users;

        foreach (string userKey in users)
        {
            notifier.Enqueue(userKey, PushPayload.FromOperator(text));
        }

        return users.Count;
    }
}
