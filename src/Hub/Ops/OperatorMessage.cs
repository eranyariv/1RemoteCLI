using System.Globalization;
using System.Text;

namespace OneRemoteCli.Hub.Ops;

/// <summary>Why an authenticated identity was turned away. An enum, so it cannot carry a sentence.</summary>
public enum RefusalKind
{
    /// <summary>A real, valid identity that is simply not on the list.</summary>
    NotAllowlisted,

    /// <summary>The token does not carry the required scope. Usually a stale app.</summary>
    MissingScope,

    /// <summary>The token validated but carries no tid/oid pair.</summary>
    NoUserKey,
}

/// <summary>Why a command was not carried out. Also an enum, and for the same reason.</summary>
public enum CommandFault
{
    Unknown,
    MissingArgument,
    NotFound,
    AlreadyDone,
    Unavailable,
}

/// <summary>One account's week. Counts and a username, nothing else.</summary>
public sealed record AccountActivity(string Account, int Sessions, long Bytes, TimeSpan Duration);

/// <summary>
/// Everything the hub is able to say to the operator, and the only way to say it.
/// <para>
/// <b>This type is the privacy rule.</b> The channel carries counts and statistics
/// only: no machine names, no session names, no terminal content, no command lines, no
/// working directories, no file paths, no machine ids and no session ids. That is not a
/// caution to be observed at each call site — it is enforced here, the way the closed
/// logging vocabulary in <c>LogEvents</c> enforces the same rule for the log file.
/// </para>
/// <para>
/// Three things make it structural rather than aspirational:
/// </para>
/// <list type="number">
/// <item>
/// The hierarchy is <b>closed</b>. The base constructor is private, so the only types
/// that can derive from it are the ones nested below. Nobody can add a message shape
/// from another file, and nobody can pass free text assembled at a call site — every
/// member takes numbers, durations, versions, an enum, or a username.
/// </item>
/// <item>
/// No member accepts a <c>SessionAddress</c>, a <c>MachineInfo</c>, a
/// <c>SessionInfo</c> or anything else that carries a name or an id. This is asserted
/// by reflection over the whole namespace in <c>OperatorVocabularyTests</c>. If the type
/// cannot reach the formatter, the leak cannot be written by accident.
/// </item>
/// <item>
/// A test renders every shape with distinctive machine and session names planted in the
/// hub around it, and asserts none of them appear.
/// </item>
/// </list>
/// <para>
/// <b>Usernames are the one identity that may appear.</b> The operator configured the
/// allowlist and already holds every address on it, "new users" and "top users" are
/// useless without them, and the highest-value message here — somebody was refused, add
/// this exact string to admit them — is nothing but an address the operator is about to
/// type anyway. Account identifiers are the operator's own material. Machine and session
/// identifiers belong to a <em>different person</em>, which is the whole difference
/// between this channel and a push notification: a push goes to the phone of the user who
/// started the session, so <c>SessionAddress</c> exposing display names is right there and
/// wrong here.
/// </para>
/// <para>
/// Rendered as plain text, not Markdown. Telegram's Markdown dialects need every one of
/// a dozen characters escaped, and an address containing an underscore would either
/// break the message or, worse, silently render as something else.
/// </para>
/// </summary>
public abstract record OperatorMessage
{
    /// <summary>
    /// Private, which is what closes the hierarchy: only the nested types below can
    /// reach it, so the vocabulary cannot be extended from anywhere else in the codebase.
    /// </summary>
    private OperatorMessage()
    {
    }

    /// <summary>The text sent to the chat.</summary>
    public abstract string Render();

    // ---------------------------------------------------------------- admission

    /// <summary>
    /// Somebody authenticated successfully and was turned away.
    /// <para>
    /// The most useful thing this channel does, and it needs no storage. Without it the
    /// person sees a failure, nobody is told, and a valid user is stuck at a dead end.
    /// With it the operator has the exact string to paste into configuration.
    /// </para>
    /// </summary>
    public sealed record AccountRefused(string Account, string? UserKey, RefusalKind Kind) : OperatorMessage
    {
        public override string Render()
        {
            var text = new StringBuilder();

            text.Append("Refused ").Append(Show(Account)).Append('.').AppendLine();
            text.AppendLine(Kind switch
            {
                RefusalKind.NotAllowlisted => "They signed in successfully and are not on the allowlist.",
                RefusalKind.MissingScope => "Their token does not carry the Session.Access scope — usually a stale app.",
                _ => "Their token carries no tenant and object id, so there is no account to allow.",
            });

            if (Kind == RefusalKind.NotAllowlisted && UserKey is not null)
            {
                text.AppendLine().AppendLine("To admit them:").Append("/allow ").AppendLine(UserKey);
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// An allowlisted account connected for the first time ever.
    /// <para>
    /// The real "new user joined". Not the config change that added them — the operator
    /// had just made that — and not the refusal, which is its own message. Needs the
    /// durable store, or every restart re-announces everybody.
    /// </para>
    /// </summary>
    public sealed record AccountFirstSeen(string Account, int AccountsEverSeen) : OperatorMessage
    {
        public override string Render() =>
            $"New user: {Show(Account)} connected for the first time.{Environment.NewLine}" +
            $"{AccountsEverSeen} {Plural(AccountsEverSeen, "account has", "accounts have")} ever connected.";
    }

    // -------------------------------------------------------------- operations

    /// <summary>
    /// The hub started.
    /// <para>
    /// Worth a message on its own, because a restart drops every push subscription and
    /// notifications stop for everyone until each phone reopens the app. Nothing tells
    /// the users this happened; their phones just go quiet. The operator should be the
    /// first to know rather than the last.
    /// </para>
    /// <para>
    /// A version that differs from the one last recorded means this restart was a
    /// deploy, which is the only way the hub can tell the two apart from inside.
    /// </para>
    /// </summary>
    public sealed record HubStarted(
        string Version,
        string? PreviousVersion,
        int Allowlisted,
        int StartsThisWeek) : OperatorMessage
    {
        public bool IsDeploy => PreviousVersion is not null && !string.Equals(PreviousVersion, Version, StringComparison.Ordinal);

        public override string Render()
        {
            var text = new StringBuilder();

            text.AppendLine(IsDeploy
                ? $"Deployed {Version} (was {PreviousVersion})."
                : $"Hub restarted on {Version}.");

            text.AppendLine("Push subscriptions were dropped; phones re-register when the app is next opened.");
            text.Append(Allowlisted).Append(Plural(Allowlisted, " account", " accounts")).Append(" allowlisted.");

            if (StartsThisWeek > 1)
            {
                text.AppendLine().Append("Start ").Append(StartsThisWeek).Append(" this week.");
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// The allowlist is empty, so the hub is denying everybody.
    /// <para>
    /// By design — an empty list means nobody rather than everybody — which makes it a
    /// total outage that looks like correct behaviour from every angle except this one.
    /// </para>
    /// </summary>
    public sealed record AllowlistEmpty : OperatorMessage
    {
        public override string Render() =>
            "The allowlist is empty, so the hub is refusing every account, including yours." +
            Environment.NewLine +
            "Nothing will connect until an account is added. Use /allow, or set Entra__Allowlist__0.";
    }

    /// <summary>Push delivery is failing at a rate worth knowing about.</summary>
    public sealed record PushFailuresSpiked(int Failures, int Expired, int WindowMinutes) : OperatorMessage
    {
        public override string Render()
        {
            var text = new StringBuilder();

            text.Append(Failures).Append(Plural(Failures, " push delivery", " push deliveries"))
                .Append(" failed in the last ").Append(WindowMinutes).Append(" minutes.");

            if (Expired > 0)
            {
                text.AppendLine().Append(Expired).Append(" of those were 404 or 410 — dead subscriptions, now dropped.");
            }

            return text.ToString();
        }
    }

    /// <summary>Tokens are being rejected at a rate worth knowing about: misconfiguration, or somebody probing.</summary>
    public sealed record TokenFailuresSpiked(int Failures, int WindowMinutes) : OperatorMessage
    {
        public override string Render() =>
            $"{Failures} token {Plural(Failures, "validation has", "validations have")} failed in the last " +
            $"{WindowMinutes} minutes. Either something is misconfigured or somebody is trying the door.";
    }

    /// <summary>An agent connected running a version well behind the hub. This is how protocol bugs start.</summary>
    public sealed record AgentVersionSkew(string AgentVersion, string HubVersion, int Agents) : OperatorMessage
    {
        public override string Render() =>
            $"{Agents} {Plural(Agents, "agent is", "agents are")} running {AgentVersion} against hub {HubVersion}.";
    }

    /// <summary>The Entra client secret is running out. Everything works until it does not.</summary>
    public sealed record ClientSecretExpiring(int Days) : OperatorMessage
    {
        public override string Render() => Days <= 0
            ? "The Entra client secret has expired. Nobody can sign in until it is renewed."
            : $"The Entra client secret expires in {Days} {Plural(Days, "day", "days")}. " +
              "When it does, nobody will be able to sign in.";
    }

    // ------------------------------------------------------------------ digest

    /// <summary>
    /// The week.
    /// <para>
    /// <see cref="Observed"/> is what the counters actually saw, against the seven days
    /// the digest claims to cover. They differ whenever the hub was restarted, and a
    /// digest that reported a partial week as a whole one would be quietly wrong every
    /// time — so it states its own coverage rather than implying complete.
    /// </para>
    /// </summary>
    public sealed record WeeklyDigest(
        DateTimeOffset From,
        DateTimeOffset To,
        int Sessions,
        long Bytes,
        TimeSpan Duration,
        int ActiveAccounts,
        IReadOnlyList<string> NewAccounts,
        IReadOnlyList<AccountActivity> TopAccounts,
        decimal Cost,
        string Currency,
        TimeSpan Observed,
        int Restarts) : OperatorMessage
    {
        /// <summary>How many of the top accounts to name. A digest is read on a phone.</summary>
        private const int Shown = 5;

        public override string Render()
        {
            var text = new StringBuilder();

            text.Append("Week of ").Append(Day(From)).Append(" to ").Append(Day(To)).AppendLine();
            text.AppendLine();

            text.Append(Sessions).AppendLine(Plural(Sessions, " session", " sessions"));
            text.Append(Size(Bytes)).AppendLine(" relayed");
            text.Append(Span(Duration)).AppendLine(" of session time");
            text.Append(ActiveAccounts).AppendLine(Plural(ActiveAccounts, " active account", " active accounts"));

            if (Cost > 0)
            {
                text.Append(Currency).Append(Money(Cost)).Append(" for the week");

                if (ActiveAccounts > 0)
                {
                    text.Append(", ").Append(Currency).Append(Money(Cost / ActiveAccounts)).Append(" per active account");
                }

                text.AppendLine();
            }

            if (NewAccounts.Count > 0)
            {
                text.AppendLine().AppendLine(NewAccounts.Count == 1 ? "New this week:" : $"New this week ({NewAccounts.Count}):");

                foreach (string account in NewAccounts.Take(Shown))
                {
                    text.Append("  ").AppendLine(Show(account));
                }
            }

            if (TopAccounts.Count > 0)
            {
                text.AppendLine().AppendLine("Most active:");

                foreach (AccountActivity account in TopAccounts.Take(Shown))
                {
                    text.Append("  ").Append(Show(account.Account))
                        .Append(" — ").Append(account.Sessions).Append(Plural(account.Sessions, " session", " sessions"))
                        .Append(", ").Append(Size(account.Bytes))
                        .Append(", ").AppendLine(Span(account.Duration));
                }
            }

            text.AppendLine().Append(Coverage());

            return text.ToString().TrimEnd();
        }

        /// <summary>
        /// What the numbers above actually cover. Said plainly whenever it is not the
        /// whole week, because "count in memory, flush on Sunday" would otherwise report
        /// a fraction as a total and never mention it.
        /// </summary>
        private string Coverage()
        {
            TimeSpan window = To - From;

            // A minute of slack. The counters start a moment after the process does, and
            // reporting "covers 167h 59m of 168h" every single week would be noise.
            if (Observed >= window - TimeSpan.FromMinutes(1) && Restarts <= 1)
            {
                return "Covers the full week.";
            }

            return $"Covers {Span(Observed)} of the {Span(window)} week — the hub was " +
                   $"running {Percent(Observed, window)} of it, across {Restarts} {Plural(Restarts, "start", "starts")}.";
        }
    }

    // ----------------------------------------------------------------- replies

    /// <summary>What the hub currently holds. Counts, never a list of machines.</summary>
    public sealed record StatusReport(
        int Machines,
        int Sessions,
        int Accounts,
        int Connections,
        TimeSpan Uptime,
        string Version) : OperatorMessage
    {
        public override string Render() =>
            $"{Machines} {Plural(Machines, "machine", "machines")} connected{Environment.NewLine}" +
            $"{Sessions} live {Plural(Sessions, "session", "sessions")}{Environment.NewLine}" +
            $"{Accounts} {Plural(Accounts, "account", "accounts")} online{Environment.NewLine}" +
            $"{Connections} {Plural(Connections, "connection", "connections")}{Environment.NewLine}" +
            $"Up {Span(Uptime)} on {Version}";
    }

    /// <summary>Whether the pieces that fail silently are configured.</summary>
    public sealed record HealthReport(
        string Version,
        TimeSpan Uptime,
        bool PushConfigured,
        int Allowlisted,
        int PushAccounts) : OperatorMessage
    {
        public override string Render() =>
            $"Version {Version}{Environment.NewLine}" +
            $"Up {Span(Uptime)}{Environment.NewLine}" +
            $"Push {(PushConfigured ? "configured" : "NOT configured")}, " +
            $"{PushAccounts} {Plural(PushAccounts, "account", "accounts")} subscribed{Environment.NewLine}" +
            $"{Allowlisted} {Plural(Allowlisted, "account", "accounts")} allowlisted";
    }

    /// <summary>Just the version, for when that is the whole question.</summary>
    public sealed record VersionReport(string Version) : OperatorMessage
    {
        public override string Render() => Version;
    }

    /// <summary>
    /// An on-demand digest was asked for.
    /// <para>
    /// Its own shape rather than a one-line report carrying the sentence, because a
    /// record with a free-text field is a hole in the guarantee this vocabulary exists
    /// to provide: whatever is in the field is whatever the caller had to hand, and no
    /// test can tell one such string from a session name.
    /// </para>
    /// </summary>
    public sealed record DigestRequested : OperatorMessage
    {
        public override string Render() => "Digest sent, and the week starts again from now.";
    }

    /// <summary>An account was added to or removed from the allowlist.</summary>
    public sealed record AllowlistChanged(string Account, bool Admitted, int Allowlisted) : OperatorMessage
    {
        public override string Render() =>
            $"{(Admitted ? "Allowed" : "Denied")} {Show(Account)}.{Environment.NewLine}" +
            $"{Allowlisted} {Plural(Allowlisted, "account", "accounts")} allowlisted. This survives a restart.";
    }

    /// <summary>Live connections for an account were closed.</summary>
    public sealed record AccountKicked(string Account, int Closed) : OperatorMessage
    {
        public override string Render() => Closed == 0
            ? $"{Show(Account)} had no live connections."
            : $"Closed {Closed} live {Plural(Closed, "connection", "connections")} for {Show(Account)}.";
    }

    /// <summary>
    /// A message was pushed to every subscribed phone.
    /// <para>
    /// The text is reported as a length, not echoed. The operator wrote it and has it on
    /// screen, and echoing arbitrary text back through the formatter is exactly the hole
    /// the closed vocabulary exists to close.
    /// </para>
    /// </summary>
    public sealed record BroadcastSent(int Accounts, int Characters) : OperatorMessage
    {
        public override string Render() => Accounts == 0
            ? "Nobody is subscribed to push, so the broadcast went nowhere."
            : $"Broadcast {Characters} {Plural(Characters, "character", "characters")} to " +
              $"{Accounts} {Plural(Accounts, "account", "accounts")}.";
    }

    /// <summary>A command could not be carried out.</summary>
    public sealed record CommandRejected(CommandFault Fault) : OperatorMessage
    {
        public override string Render() => Fault switch
        {
            CommandFault.MissingArgument => "That command needs an argument. Send /help.",
            CommandFault.NotFound => "No account matches that. It has to be an email address or a {tid}:{oid} key.",
            CommandFault.AlreadyDone => "Already in that state; nothing changed.",
            CommandFault.Unavailable => "That is not available on this hub.",
            _ => "Unknown command. Send /help.",
        };
    }

    /// <summary>
    /// The command list.
    /// <para>
    /// Note what is absent. There is deliberately nothing that enumerates machines or
    /// sessions by name — an admin console is exactly where that would feel reasonable
    /// to add, so it is ruled out here rather than left to judgement later.
    /// </para>
    /// </summary>
    public sealed record Help : OperatorMessage
    {
        public override string Render() =>
            "/status — machines, sessions, accounts online, uptime" + Environment.NewLine +
            "/health — version, uptime, push and allowlist configuration" + Environment.NewLine +
            "/version — the running version" + Environment.NewLine +
            "/allow <email or key> — add an account to the allowlist" + Environment.NewLine +
            "/deny <email or key> — remove an account, and close its connections" + Environment.NewLine +
            "/kick <email or key> — close an account's connections without removing it" + Environment.NewLine +
            "/broadcast <text> — push a message to every subscribed phone" + Environment.NewLine +
            "/digest — send the weekly digest now" + Environment.NewLine +
            Environment.NewLine +
            "This channel reports counts only. Machines and sessions are never named here.";
    }

    // ------------------------------------------------------------- formatting

    /// <summary>
    /// An account with nothing at all is still an event worth reporting, so it renders
    /// as a placeholder rather than an empty gap in a sentence.
    /// </summary>
    private static string Show(string account) =>
        string.IsNullOrWhiteSpace(account) ? "(no username)" : account;

    private static string Plural(long count, string one, string many) => count == 1 ? one : many;

    private static string Day(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(TimeSpan part, TimeSpan whole) => whole <= TimeSpan.Zero
        ? "0%"
        : ((int)Math.Round(100 * part.TotalSeconds / whole.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Bytes, and labelled bytes.
    /// <para>
    /// The request was for characters, and these are not the same thing: the hub relays
    /// UTF-8, where anything outside ASCII costs two to four bytes, and it never decodes
    /// the stream — keeping terminal output opaque is what lets end-to-end encryption be
    /// added later. Counting bytes and saying "bytes" is the honest version of the
    /// number; counting bytes and calling them characters would not be.
    /// </para>
    /// </summary>
    internal static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>A duration a human reads at a glance, rather than 1.03:47:12.9981234.</summary>
    internal static string Span(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds}s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        if (duration.TotalDays < 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }
}
