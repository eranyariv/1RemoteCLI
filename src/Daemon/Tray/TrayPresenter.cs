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
/// <param name="SignInEnabled">Whether <em>Sign in</em> does anything useful right now.</param>
public readonly record struct TrayPresentation(string Tooltip, AgentState Badge, bool SignInEnabled);

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

    public static TrayPresentation Present(AgentState state, int sessionCount, string machineName)
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

        return new TrayPresentation(
            Truncate($"{headline}{name}\n{detail}"),
            state,
            SignInEnabled: state != AgentState.Connected);
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
