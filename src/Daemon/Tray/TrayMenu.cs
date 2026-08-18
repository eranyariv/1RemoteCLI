using OneRemoteCli.Protocol;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>What choosing a menu item should do.</summary>
public enum TrayCommand
{
    /// <summary>Not a command: a separator, or a label that exists to be read.</summary>
    None,
    SignIn,
    SwitchAccount,
    SignOut,
    ShowSessions,
    OpenLogs,
    SendFeedback,
    Quit,
}

/// <summary>One line of the tray menu.</summary>
/// <param name="Command">What to run, or <see cref="TrayCommand.None"/> for a separator or label.</param>
/// <param name="Text">What it says. Empty means a separator.</param>
/// <param name="Enabled">Whether it can be chosen.</param>
public readonly record struct TrayMenuItem(TrayCommand Command, string Text, bool Enabled)
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
/// </summary>
public static class TrayMenu
{
    public static IReadOnlyList<TrayMenuItem> Build(TrayPresentation view) =>
    [
        // Who, before what. The account is first because it is the one fact the icon
        // cannot show and the one people get wrong.
        TrayMenuItem.Label(view.Account),
        TrayMenuItem.Separator,

        new(TrayCommand.SignIn, "Sign in", view.SignInEnabled),
        new(TrayCommand.SwitchAccount, "Sign in as a different account\u2026", view.SignOutEnabled),
        new(TrayCommand.SignOut, "Sign out", view.SignOutEnabled),
        TrayMenuItem.Separator,

        new(TrayCommand.ShowSessions, "Show sessions", true),
        new(TrayCommand.OpenLogs, "Open logs", true),
        new(TrayCommand.SendFeedback, "Send feedback\u2026", true),
        TrayMenuItem.Separator,

        // A label rather than a command. It is here because the tray is the only part
        // of the agent a user ever looks at, and "which version are you running" is the
        // first question any report needs answered.
        TrayMenuItem.Label($"Version {ProductVersion.Current}"),
        TrayMenuItem.Separator,

        new(TrayCommand.Quit, "Quit", true),
    ];
}
