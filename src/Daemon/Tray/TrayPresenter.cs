using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>
/// What the agent is doing, as far as anyone looking at the tray is concerned.
/// <para>
/// Three states, not five. The distinction a user acts on is "is my phone going to
/// see this machine" — and the only three answers are yes, not yet, and not until
/// you do something. Splitting "connecting" from "reconnecting", or "no network"
/// from "hub is down", produces states that look different and lead to the same
/// action.
/// </para>
/// </summary>
public enum AgentState
{
    /// <summary>No token. The phone cannot see this machine and only the user can fix it.</summary>
    SignedOut,

    /// <summary>Trying. Normal at logon, and normal on a train.</summary>
    Reconnecting,

    /// <summary>Reachable.</summary>
    Connected,
}

/// <summary>How the tray should look and what its menu should offer.</summary>
/// <param name="Tooltip">The hover text. First line is the state, second the detail.</param>
/// <param name="Badge">The icon's state, which the renderer maps to a colour.</param>
/// <param name="Account">
/// The menu's first line: who is signed in. Shown even though the icon already
/// implies it, because "which account is this?" is a question the icon cannot answer
/// and the answer is not always the one the user expected — a browser already signed
/// in to a work account will happily have supplied it.
/// </param>
/// <param name="SignInEnabled">Whether <em>Sign in</em> does anything useful right now.</param>
/// <param name="SignOutEnabled">Whether there is an account to sign out of, or switch away from.</param>
/// <param name="UpdateVersion">
/// The release waiting to be installed, or null when there is none. Null for every
/// stage but <see cref="Update.UpdateStage.Available"/>: a check in progress, a failed
/// one and an update already installed are all things the menu should stay silent
/// about, because none of them is something clicking would advance.
/// </param>
public readonly record struct TrayPresentation(
    string Tooltip,
    AgentState Badge,
    string Account,
    bool SignInEnabled,
    bool SignOutEnabled,
    string? UpdateVersion = null);

/// <summary>
/// Turning agent state into what the tray shows.
/// <para>
/// Pure, and separate from the icon, because the icon cannot be asserted on and this
/// can. It is also the only part a user reads: the tooltip is the entire diagnostic
/// surface for somebody whose phone has stopped seeing their machine.
/// </para>
/// </summary>
public static class TrayPresenter
{
    /// <summary>
    /// Windows truncates a tray tooltip at 127 characters and drops anything longer,
    /// so the machine name — which can be arbitrarily long — is what would get cut.
    /// </summary>
    public const int TooltipLimit = 127;

    public static TrayPresentation Present(
        AgentState state,
        int sessionCount,
        string machineName,
        string? account = null,
        UpdateStatus update = default)
    {
        string headline = state switch
        {
            AgentState.Connected => "1RemoteCLI: connected",
            AgentState.Reconnecting => "1RemoteCLI: reconnecting",
            _ => "1RemoteCLI: signed out",
        };

        string detail = state switch
        {
            // The session count is the answer to "is it actually working", and the one
            // number the user can check against what they know they started.
            AgentState.Connected => sessionCount switch
            {
                0 => "No sessions. Run '1remote pwsh' to share one.",
                1 => "1 session, reachable from your phone.",
                _ => $"{sessionCount} sessions, reachable from your phone.",
            },
            AgentState.Reconnecting => "Sessions here keep working; your phone cannot see them yet.",
            _ => "Sign in so your phone can see this machine.",
        };

        string name = string.IsNullOrWhiteSpace(machineName) ? string.Empty : $" ({machineName})";

        bool signedIn = !string.IsNullOrWhiteSpace(account);

        return new TrayPresentation(
            Truncate($"{headline}{name}\n{detail}"),
            state,
            // Keyed off the account rather than the connection: a machine on a train is
            // reconnecting, not signed out, and offering to sign in an account that is
            // already signed in would send the user round a loop that changes nothing.
            Account: signedIn ? $"Signed in as {account}" : "Not signed in",
            SignInEnabled: !signedIn,
            SignOutEnabled: signedIn,
            UpdateVersion: update.CanInstall ? update.Version : null);
    }

    /// <summary>
    /// Trims to what Windows will show. The headline comes first for exactly this
    /// reason: a tooltip cut short still says what is wrong.
    /// </summary>
    private static string Truncate(string tooltip) =>
        tooltip.Length <= TooltipLimit
            ? tooltip
            : string.Concat(tooltip.AsSpan(0, TooltipLimit - 1), "\u2026");
}
