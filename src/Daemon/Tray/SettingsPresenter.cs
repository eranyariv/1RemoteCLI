using OneRemoteCli.Daemon.Update;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>The semantic colour of a status orb in the settings window.</summary>
public enum StatusTone
{
    Disabled,
    Connecting,
    Good,
    Error,
}

/// <summary>One live session, reduced to what the settings window shows.</summary>
/// <param name="DisplayName">What the user called it, or the program name.</param>
/// <param name="StartedUtc">When a terminal began, or when an agent chat was last updated.</param>
/// <param name="AwaitingInput">
/// Whether it looks like it is waiting for an answer right now. The same judgement
/// that decides whether to notify a phone, asked again rather than remembered: the
/// notification fires once per quiet episode, and a window would then keep showing
/// "waiting" for a session the user already answered.
/// </param>
/// <param name="CliType">
/// Which CLI it is hosting, as the agent guessed it. Read-only here, like every other
/// line in this window: it decides which buttons a phone offers, and the phone is
/// where it is corrected. <see cref="Protocol.Hub.CliType.Generic"/> is written as
/// nothing at all — see <see cref="SettingsPresenter.Describe(SessionSummary, DateTimeOffset)"/>.
/// </param>
/// <param name="Kind">Whether this is a wrapped terminal or an ACP-backed chat.</param>
/// <param name="Program">The ACP provider name for a chat.</param>
/// <param name="Cwd">The session's working directory.</param>
public readonly record struct SessionSummary(
    string DisplayName,
    DateTimeOffset StartedUtc,
    bool AwaitingInput,
    CliType CliType = CliType.Generic,
    SessionKind Kind = SessionKind.Terminal,
    string Program = "",
    string Cwd = "");

public sealed record SessionRow(
    string Name,
    string Source,
    string Folder,
    string Status,
    string Activity,
    DateTimeOffset ActivityAt);

/// <summary>Everything the settings window reads, as text.</summary>
/// <param name="Account">Who is signed in, or that nobody is.</param>
/// <param name="Connection">Whether the phone can see this machine, and what to do if not.</param>
/// <param name="SignedIn">Which of sign-in and sign-out to offer.</param>
/// <param name="AccountTone">The account status orb.</param>
/// <param name="ConnectionTone">The hub connection status orb.</param>
/// <param name="HasSessions">
/// False when <paramref name="Sessions"/> holds the empty-state sentence rather than
/// sessions, so the window can show it without making it look selectable.
/// </param>
/// <param name="Sessions">Structured rows for the sortable local-session table.</param>
/// <param name="Version">The build, which is the first thing any report needs.</param>
/// <param name="Update">
/// What the agent knows about newer releases, as a sentence. Empty when there is
/// nothing to say, so the window can leave the line out rather than print "no update".
/// </param>
/// <param name="CanUpdate">Whether the update button does anything right now.</param>
/// <param name="CanCheckForUpdates">Whether another update check can start now.</param>
public readonly record struct SettingsView(
    string Account,
    string Connection,
    bool SignedIn,
    StatusTone AccountTone,
    StatusTone ConnectionTone,
    bool HasSessions,
    IReadOnlyList<SessionRow> Sessions,
    string Version,
    string Update = "",
    bool CanUpdate = false,
    bool CanCheckForUpdates = true);

/// <summary>
/// What the settings window says.
/// <para>
/// Pure, and separate from the window, for the same reason <see cref="TrayPresenter"/>
/// is separate from the icon: a Win32 window cannot be asserted on and this can. Every
/// sentence here is one somebody reads at the moment their phone has stopped seeing
/// their machine, which is the worst possible time for it to be vague.
/// </para>
/// </summary>
public static class SettingsPresenter
{
    public const string Title = "1RemoteCLI";

    public const string SignInLabel = "Sign in";

    public const string SignOutLabel = "Sign out";

    public const string StatusTabLabel = "Status";

    public const string SessionsTabLabel = "Local sessions";

    public const string SettingsTabLabel = "Settings";

    /// <summary>
    /// Worded as the thing that happens rather than as a setting name. "Start at
    /// logon" is jargon for the same sentence.
    /// </summary>
    public const string StartAtLogonLabel = "Start when I sign in to Windows";

    public const string WrapShortcutLabel = "Wrap a desktop shortcut\u2026";

    public const string OpenLogsLabel = "Open logs";

    public const string SendFeedbackLabel = "Send feedback\u2026";

    public const string ChangeHistoryLabel = "Change history";

    public const string CloseLabel = "Close";

    /// <summary>
    /// Shown in place of the list. It names the command, because somebody looking at
    /// an empty list has not yet worked out that sessions are something they start.
    /// </summary>
    public const string NoSessions =
        "No local sessions. Run '1remote pwsh' or start a Copilot or Claude chat.";

    /// <summary>
    /// The update button. It says what will happen rather than what state the machine
    /// is in, because the line above it already says the state.
    /// </summary>
    public const string UpdateLabel = "Update now";

    public const string CheckForUpdatesLabel = "Check for updates";

    public static SettingsView Present(
        AgentState state,
        string? account,
        IReadOnlyList<SessionSummary> sessions,
        DateTimeOffset now,
        UpdateStatus update = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        bool signedIn = !string.IsNullOrWhiteSpace(account);

        string connection = state switch
        {
            AgentState.Connected => "Connected. Your phone can see this machine.",
            AgentState.Reconnecting => "Reconnecting. Sessions here keep working; your phone cannot see them yet.",
            _ => "Signed out. Your phone cannot see this machine.",
        };

        SessionRow[] rows = [.. sessions.Select(session => DescribeRow(session, now))];

        return new SettingsView(
            // Keyed off the account rather than the connection: a machine on a train is
            // reconnecting, not signed out, and offering to sign in an account that is
            // already signed in would send the user round a loop that changes nothing.
            signedIn ? $"Signed in as {account}" : "Not signed in",
            connection,
            signedIn,
            signedIn ? StatusTone.Good : StatusTone.Disabled,
            state switch
            {
                AgentState.Connected => StatusTone.Good,
                AgentState.Reconnecting => StatusTone.Connecting,
                _ => StatusTone.Disabled,
            },
            rows.Length > 0,
            rows,
            $"Version {ProductVersion.Current}",
            Describe(update),
            update.CanInstall,
            update.CanCheck);
    }

    public static string SessionsHeading(int count) =>
        count == 1
            ? "1 session discovered on this machine"
            : $"{count} sessions discovered on this machine";

    /// <summary>
    /// What the window says about newer releases.
    /// <para>
    /// Empty only before the first check. Once the user asks, every outcome remains
    /// visible so a completed no-op cannot look like an ignored click.
    /// </para>
    /// <para>
    /// A failed check does say so. It is the difference between an agent that has
    /// looked and found nothing and one that has not been able to look for a month,
    /// and only the second is worth acting on.
    /// </para>
    /// </summary>
    public static string Describe(UpdateStatus update) => update.Stage switch
    {
        UpdateStage.UpToDate => "You're up to date.",
        UpdateStage.Checking => "Checking for updates\u2026",

        // The version, not "an update": it is what tells somebody whether this is the
        // release with the fix they have been waiting for.
        UpdateStage.Available => $"Version {update.Version} is available.",
        UpdateStage.Installing => $"Installing version {update.Version}\u2026",

        // The message carries the reason when there are sessions in the way, and that
        // reason is the whole content of the line.
        UpdateStage.Restart => update.Message is { Length: > 0 } waiting
            ? waiting
            : $"Version {update.Version} is installed. Restarting\u2026",

        UpdateStage.Failed => update.Message ?? "The last check for updates did not work.",
        _ => string.Empty,
    };

    /// <summary>
    /// One session as a line: what it is, what it is running, how long it has been
    /// there, and whether it wants something. In that order, because the first is how
    /// the user finds the one they are looking for and the last is why they opened the
    /// window.
    /// <para>
    /// A type we could not work out is left out rather than written as "Generic".
    /// Naming the absence of an answer costs a reader a moment on every line to learn
    /// nothing, and does it on the lines that are already the least informative.
    /// </para>
    /// </summary>
    public static string Describe(SessionSummary session, DateTimeOffset now)
    {
        string kind;
        string age;

        if (session.Kind == SessionKind.AgentChat)
        {
            kind = $"{Provider(session)} chat \u2014 ";
            age = $"updated {Since(now - session.StartedUtc)}";
        }
        else
        {
            kind = session.CliType == CliType.Generic
                ? string.Empty
                : $"{CliTypes.Label(session.CliType)} \u2014 ";
            age = $"started {Since(now - session.StartedUtc)}";
        }

        string line = $"{session.DisplayName} \u2014 {kind}{age}";

        return session.AwaitingInput ? $"{line} \u2014 waiting for input" : line;
    }

    public static SessionRow DescribeRow(SessionSummary session, DateTimeOffset now)
    {
        bool chat = session.Kind == SessionKind.AgentChat;
        string source = chat
            ? $"{Provider(session)} chat"
            : session.CliType == CliType.Generic
                ? ProgramName(session.Program)
                : CliTypes.Label(session.CliType);

        return new SessionRow(
            session.DisplayName,
            source,
            session.Cwd,
            session.AwaitingInput ? "Waiting for input" : chat ? "Available" : "Running",
            $"{(chat ? "Updated" : "Started")} {Since(now - session.StartedUtc)}",
            session.StartedUtc);
    }

    private static string Provider(SessionSummary session) =>
        string.IsNullOrWhiteSpace(session.Program) ? CliTypes.Label(session.CliType) : session.Program;

    private static string ProgramName(string program)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(program);
            return string.IsNullOrWhiteSpace(name) ? "Terminal" : name;
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(program) ? "Terminal" : program;
        }
    }

    /// <summary>
    /// How long ago, in the coarsest unit that is still true.
    /// <para>
    /// Nobody reading this list cares about seconds; they care whether the thing they
    /// started this morning is still there. A negative age — a clock that moved
    /// backwards, or a session started microseconds ago — reads as "just now" rather
    /// than as a negative number.
    /// </para>
    /// </summary>
    public static string Since(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        return Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int count, string unit) =>
        count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
