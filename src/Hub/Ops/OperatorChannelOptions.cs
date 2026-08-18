namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// The Telegram chat the hub reports to, and the few numbers it needs to report
/// anything useful.
/// <para>
/// Absent by default, and absent is a supported state rather than a failure — the same
/// rule the VAPID keypair follows. Nobody should have to provision a bot to work on the
/// relay, so an unconfigured hub simply has no operator channel and says so once at
/// startup.
/// </para>
/// <para>
/// The bot token is a credential and never source. It arrives as the App Service
/// setting <c>Telegram__BotToken</c>, and there is deliberately no default and no
/// sample value anywhere in the repository. Once inbound commands are enabled it stops
/// being a write-only notification credential and becomes an administrative one, which
/// is why <see cref="ChatId"/> is checked on every update rather than trusted.
/// </para>
/// </summary>
public sealed class OperatorChannelOptions
{
    public const string Section = "Telegram";

    /// <summary>The bot's API token, as BotFather issued it. A secret.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// The one chat that receives reports and the only one whose messages are obeyed.
    /// <para>
    /// A single value rather than a list: this is the operator's own chat, and "which
    /// of these several administrators sent this" is a question with no good answer at
    /// this scale.
    /// </para>
    /// </summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>
    /// Whether to poll for and act on inbound commands.
    /// <para>
    /// Separate from the token because the two carry different risk. Reporting is
    /// write-only; commands can change who may sign in. A hub that only reports leaves
    /// the token unable to do anything but talk.
    /// </para>
    /// </summary>
    public bool Commands { get; set; }

    /// <summary>Which day the weekly digest is sent.</summary>
    public DayOfWeek DigestDay { get; set; } = DayOfWeek.Monday;

    /// <summary>The hour, UTC, the digest is sent on that day.</summary>
    public int DigestHourUtc { get; set; } = 8;

    /// <summary>
    /// What this deployment costs per month, in whatever currency the operator thinks
    /// in.
    /// <para>
    /// Configured rather than queried. The honest number is Azure spend for the resource
    /// group from the Cost Management API, but that needs an ARM credential this hub does
    /// not have and should not be given — a bot token that can also read a subscription's
    /// billing is a much larger thing to lose. The plan is a fixed monthly charge, so a
    /// configured figure is exact anyway, and the number that actually informs a decision
    /// is cost per active user, which is this divided by a count the hub does know.
    /// </para>
    /// </summary>
    public decimal MonthlyCost { get; set; }

    /// <summary>The currency symbol to print in front of it. Cosmetic.</summary>
    public string Currency { get; set; } = "$";

    /// <summary>
    /// When the Entra client secret expires, if the operator has recorded it.
    /// <para>
    /// Configured for the same reason as the cost: the hub validates tokens against
    /// public signing keys and never holds the secret, so it cannot discover this. But
    /// "everything works until one morning nobody can sign in" is exactly the failure
    /// this channel exists to prevent, and a date typed in once at renewal is enough.
    /// </para>
    /// </summary>
    public DateTimeOffset? ClientSecretExpiresOn { get; set; }

    /// <summary>
    /// Where the durable counters live. Defaults to the platform's persistent volume.
    /// <para>
    /// See <see cref="OperatorStateStore"/> for why a file rather than a database.
    /// </para>
    /// </summary>
    public string StatePath { get; set; } = string.Empty;

    /// <summary>True when there is somewhere to send a message.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(BotToken) &&
        !string.IsNullOrWhiteSpace(ChatId);

    /// <summary>Inbound is only live when it has been asked for and there is a token to poll with.</summary>
    public bool CommandsEnabled => Configured && Commands;
}
