namespace OneRemoteCli.Daemon.Tray;

/// <summary>What choosing a menu item should do.</summary>
public enum TrayCommand
{
    /// <summary>Not a command: a separator, or a label that exists to be read.</summary>
    None,
    Settings,
    ShowSessions,
    Quit,
}

/// <summary>One line of the tray menu.</summary>
/// <param name="Command">What to run, or <see cref="TrayCommand.None"/> for a separator or label.</param>
/// <param name="Text">What it says. Empty means a separator.</param>
/// <param name="Enabled">Whether it can be chosen.</param>
/// <param name="IsDefault">
/// Whether this is what double-clicking the icon does. The shell draws it in bold,
/// which is the only thing that makes that shortcut discoverable.
/// </param>
public readonly record struct TrayMenuItem(
    TrayCommand Command,
    string Text,
    bool Enabled,
    bool IsDefault = false)
{
    public static TrayMenuItem Separator { get; } = new(TrayCommand.None, string.Empty, false);

    public static TrayMenuItem Label(string text) => new(TrayCommand.None, text, false);

    public bool IsSeparator => Text.Length == 0;
}

/// <summary>
/// The tray menu, as a list rather than as a tree of live widgets.
/// <para>
/// Built fresh from the current <see cref="TrayPresentation"/> every time the user
/// opens it. The alternative — holding onto each item and updating its text and
/// enabled flag as state changes — is what the Windows Forms version did, and it has
/// a failure mode this cannot have: an item whose handle was missed silently keeps
/// showing the last thing that was true.
/// </para>
/// <para>
/// Being a plain list also makes the menu assertable. It is the only part of the tray
/// a user interacts with, and until now none of it could be tested.
/// </para>
/// <para>
/// Deliberately short. Signing in and out, the logs, feedback and the version all
/// moved into the settings window (issue #73); keeping them here as well would mean
/// two places saying whether you are signed in, and the one that was wrong would be
/// the one somebody read.
/// </para>
/// </summary>
public static class TrayMenu
{
    /// <summary>
    /// What double-clicking the icon does.
    /// <para>
    /// Declared here, next to the item that is drawn in bold, because the bold is a
    /// promise about this: two places naming the action separately is how a menu comes
    /// to advertise a shortcut that does something else.
    /// </para>
    /// </summary>
    public const TrayCommand DefaultCommand = TrayCommand.Settings;

    public static IReadOnlyList<TrayMenuItem> Build(TrayPresentation view) =>
    [
        // Who, before what. The account is first because it is the one fact the icon
        // cannot show and the one people get wrong. Still a label, not a command —
        // changing it is now the first thing in the window below.
        TrayMenuItem.Label(view.Account),
        TrayMenuItem.Separator,

        // Bold, because this is what double-clicking the icon does. It is the settings
        // window rather than the web app: this menu is opened when something looks
        // wrong, and the window is the only place that says what.
        new(TrayCommand.Settings, "Settings\u2026", true, IsDefault: DefaultCommand == TrayCommand.Settings),
        new(TrayCommand.ShowSessions, "Open the web app", true),
        TrayMenuItem.Separator,

        new(TrayCommand.Quit, "Quit", true),
    ];
}
