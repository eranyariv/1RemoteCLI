using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>One live session, reduced to what the settings window shows.</summary>
/// <param name="DisplayName">What the user called it, or the program name.</param>
/// <param name="StartedUtc">When it began.</param>
/// <param name="AwaitingInput">
/// Whether it looks like it is waiting for an answer right now. The same judgement
/// that decides whether to notify a phone, asked again rather than remembered: the
/// notification fires once per quiet episode, and a window would then keep showing
/// "waiting" for a session the user already answered.
/// </param>
/// <param name="CliType">
/// Which CLI it is hosting. Shown even when it is <see cref="Protocol.Hub.CliType.Generic"/>,
/// because "we could not tell" is the state that explains why the phone is offering
/// no shortcuts, and a user who cannot see it has no reason to go and set one.
/// </param>
public readonly record struct SessionSummary(
    string DisplayName,
    DateTimeOffset StartedUtc,
    bool AwaitingInput,
    CliType CliType = CliType.Generic);

/// <summary>Everything the settings window reads, as text.</summary>
/// <param name="Account">Who is signed in, or that nobody is.</param>
/// <param name="Connection">Whether the phone can see this machine, and what to do if not.</param>
/// <param name="SignedIn">Which of sign-in and sign-out to offer.</param>
/// <param name="HasSessions">
/// False when <paramref name="Sessions"/> holds the empty-state sentence rather than
/// sessions, so the window can show it without making it look selectable.
/// </param>
/// <param name="Sessions">One line per session, oldest first.</param>
/// <param name="Version">The build, which is the first thing any report needs.</param>
public readonly record struct SettingsView(
    string Account,
    string Connection,
    bool SignedIn,
    bool HasSessions,
    IReadOnlyList<string> Sessions,
    string Version);

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

    public const string SessionsLabel = "Sessions on this machine";

    /// <summary>
    /// Worded as the thing that happens rather than as a setting name. "Start at
    /// logon" is jargon for the same sentence.
    /// </summary>
    public const string StartAtLogonLabel = "Start when I sign in to Windows";

    public const string WrapShortcutLabel = "Wrap a desktop shortcut\u2026";

    public const string OpenLogsLabel = "Open logs";

    public const string SendFeedbackLabel = "Send feedback\u2026";

    public const string CloseLabel = "Close";

    /// <summary>
    /// Shown in place of the list. It names the command, because somebody looking at
    /// an empty list has not yet worked out that sessions are something they start.
    /// </summary>
    public const string NoSessions = "No sessions. Run '1remote pwsh' to share one.";

    public static SettingsView Present(
        AgentState state,
        string? account,
        IReadOnlyList<SessionSummary> sessions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        bool signedIn = !string.IsNullOrWhiteSpace(account);

        string connection = state switch
        {
            AgentState.Connected => "Connected. Your phone can see this machine.",
            AgentState.Reconnecting => "Reconnecting. Sessions here keep working; your phone cannot see them yet.",
            _ => "Signed out. Your phone cannot see this machine.",
        };

        string[] lines = [.. sessions.OrderBy(session => session.StartedUtc).Select(session => Describe(session, now))];

        return new SettingsView(
            // Keyed off the account rather than the connection: a machine on a train is
            // reconnecting, not signed out, and offering to sign in an account that is
            // already signed in would send the user round a loop that changes nothing.
            signedIn ? $"Signed in as {account}" : "Not signed in",
            connection,
            signedIn,
            lines.Length > 0,
            lines.Length > 0 ? lines : [NoSessions],
            $"Version {ProductVersion.Current}");
    }

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
        string kind = session.CliType == CliType.Generic
            ? string.Empty
            : $"{CliTypes.Label(session.CliType)} \u2014 ";

        string line = $"{session.DisplayName} \u2014 {kind}started {Since(now - session.StartedUtc)}";

        return session.AwaitingInput ? $"{line} \u2014 waiting for input" : line;
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
