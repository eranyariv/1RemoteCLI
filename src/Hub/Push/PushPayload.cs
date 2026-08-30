using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Hub.Push;

[Flags]
public enum PushNotificationKinds
{
    None = 0,
    AwaitingInput = 1,
    SessionFinished = 2,
    Announcement = 4,
}

/// <summary>
/// What the service worker is handed, and the deep link that makes a notification
/// worth tapping.
/// <para>
/// The target experience is two taps from a locked phone: tap the notification, tap
/// <c>y</c>. Everything here exists to serve that - the URL carries enough to open
/// straight into the right session, so the user never sees the machine list on the
/// way. A notification that lands you on a home screen you then have to navigate is
/// barely better than no notification at all.
/// </para>
/// <para>
/// Built as a plain object and serialised here rather than assembled in the hub, so
/// the shape the phone receives has one definition and can be asserted in a test
/// instead of being discovered on a device.
/// </para>
/// </summary>
public sealed record PushPayload
{
    [JsonIgnore]
    public PushNotificationKinds Kind { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    /// <summary>Where tapping it lands, relative to the app's origin.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "/";

    /// <summary>
    /// Groups notifications so a later one replaces an earlier one.
    /// <para>
    /// Per session. A session that asks twice should occupy one row on the lock
    /// screen showing the current question, not two rows the user has to read in
    /// order to find out which is still true.
    /// </para>
    /// </summary>
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    /// <summary>
    /// True when the notification is only worth seeing if it is still true.
    /// <para>
    /// Set for "waiting for input": if the phone was off and the prompt has since
    /// been answered, showing it on wake is a lie. Not set for "finished", which
    /// remains true however late it arrives.
    /// </para>
    /// </summary>
    [JsonPropertyName("perishable")]
    public bool Perishable { get; init; }

    public static PushPayload AwaitingInput(string machineName, string sessionName, string? hint, string deepLink) =>
        new()
        {
            Kind = PushNotificationKinds.AwaitingInput,
            Title = $"{sessionName} is waiting",
            // The hint is the prompt itself, which is the single most useful thing to
            // put on a lock screen: the user knows what they started, not what it
            // decided to ask.
            Body = string.IsNullOrWhiteSpace(hint) ? $"On {machineName}." : hint,
            Url = deepLink,
            Tag = deepLink,
            Perishable = true,
        };

    public static PushPayload Finished(string machineName, string sessionName, int exitCode, string deepLink) =>
        new()
        {
            Kind = PushNotificationKinds.SessionFinished,
            Title = exitCode == 0 ? $"{sessionName} finished" : $"{sessionName} failed",
            Body = exitCode == 0 ? $"On {machineName}." : $"Exit code {exitCode}, on {machineName}.",
            Url = deepLink,
            Tag = deepLink,
        };

    /// <summary>
    /// A message from whoever runs this hub, to everybody.
    /// <para>
    /// The only payload not about a session, so it has no deep link and lands on the app
    /// itself. One fixed tag, so a second announcement replaces the first rather than
    /// stacking: an operator sending a correction wants the correction read, not both.
    /// </para>
    /// <para>
    /// Not perishable. "The hub is down for ten minutes" is worth reading late, which is
    /// exactly when somebody whose phone was off most needs it.
    /// </para>
    /// </summary>
    public static PushPayload FromOperator(string text) =>
        new()
        {
            Kind = PushNotificationKinds.Announcement,
            Title = "1RemoteCLI",
            Body = text,
            Url = "/",
            Tag = "operator",
        };

    /// <summary>The deep link for one session. Query rather than path: the app is a single page.</summary>
    public static string DeepLink(string machineId, string sessionId) =>
        $"/?machine={Uri.EscapeDataString(machineId)}&session={Uri.EscapeDataString(sessionId)}";

    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    private static class PayloadJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }
}
