using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Push;

/// <summary>Delivers one payload to every device a user has registered.</summary>
public interface IPushSender
{
    ValueTask SendAsync(string userKey, PushPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Web Push over VAPID.
/// <para>
/// The encryption is RFC 8291 - ECDH against the subscriber's key, HKDF, AES-128-GCM -
/// and is left to a library rather than written here. Getting it subtly wrong produces
/// a request the push service accepts and a phone that never rings, which is the worst
/// possible failure mode: invisible in every test that does not involve a real device.
/// </para>
/// </summary>
public sealed class WebPushSender(
    PushSubscriptionStore store,
    PushServiceClient client,
    IOptions<VapidOptions> options,
    OneRemoteCli.Hub.Ops.FailureRates failures,
    ILogger<WebPushSender> logger) : IPushSender
{
    private readonly PushSubscriptionStore _store = store;
    private readonly PushServiceClient _client = client;
    private readonly VapidOptions _vapid = options.Value;
    private readonly OneRemoteCli.Hub.Ops.FailureRates _failures = failures;
    private readonly ILogger<WebPushSender> _logger = logger;

    public async ValueTask SendAsync(
        string userKey,
        PushPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_vapid.Configured)
        {
            return;
        }

        IReadOnlyList<PushSubscription> registered = _store.For(userKey);
        if (registered.Count == 0)
        {
            // Worth saying out loud. The single most likely cause is that the hub was
            // restarted and the subscriptions went with it, and "no notifications" is
            // otherwise indistinguishable from a dozen other causes.
            _logger.LogDebug("No push subscriptions registered; nothing to notify.");
            return;
        }

        IReadOnlyList<PushSubscription> subscriptions =
            [.. registered.Where(subscription => subscription.Allows(payload.Kind))];
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("Every registered device disabled this notification category.");
            return;
        }

        var message = new PushMessage(payload.ToJson())
        {
            // A prompt is worth waking the screen for; a phone that batches this until
            // the next time it happens to sync has delivered nothing of value.
            Urgency = PushMessageUrgency.High,

            // Perishable messages expire rather than arriving hours late about a
            // question that has since been answered.
            TimeToLive = payload.Perishable ? 600 : 3600,

            // Coalesces at the push service too, not only on the device: a second
            // notification for the same session replaces the first in the queue.
            Topic = Topic(payload.Tag),
        };

        await Task.WhenAll(subscriptions.Select(subscription =>
            DeliverAsync(userKey, subscription, message, cancellationToken))).ConfigureAwait(false);
    }

    private async Task DeliverAsync(
        string userKey,
        PushSubscription subscription,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        var target = new Lib.Net.Http.WebPush.PushSubscription { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

        try
        {
            await _client.RequestPushMessageDeliveryAsync(
                target,
                message,
                new VapidAuthentication(_vapid.PublicKey, _vapid.PrivateKey) { Subject = _vapid.Subject },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PushServiceClientException ex) when (Expired(ex.StatusCode))
        {
            // The app was uninstalled, or the browser rotated the subscription. Keeping
            // it would mean one failed request per notification for the life of the
            // process, and the phone will register again next time it opens the app.
            _store.Forget(userKey, subscription.Endpoint);
            _logger.LogInformation("Dropped an expired push subscription ({Status}).", ex.StatusCode);

            // Counted, because one is routine and a run of them is not: a restart drops
            // every subscription, and the first sign is a burst of 410s.
            _failures.PushFailed(expired: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fatal. A push service being unreachable must not affect the relay,
            // which is the half of the product that has a user waiting on it.
            _logger.LogWarning(ex, "Push delivery failed.");
            _failures.PushFailed(expired: false);
        }
    }

    private static bool Expired(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>
    /// A push topic must be a short base64url token, which a deep link is not.
    /// <para>
    /// Hashed rather than truncated: two sessions on the same machine share a long
    /// prefix, and truncation would collapse them into one topic - so answering one
    /// prompt would silently discard the notification about the other.
    /// </para>
    /// </summary>
    private static string Topic(string tag)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));
        return WebEncoders(hash)[..22];
    }

    private static string WebEncoders(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Push is not configured. Every send is a no-op, and the hub said so at startup.</summary>
public sealed class DisabledPushSender : IPushSender
{
    public ValueTask SendAsync(
        string userKey,
        PushPayload payload,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
