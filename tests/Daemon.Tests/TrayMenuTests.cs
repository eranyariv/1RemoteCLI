using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tray menu's contents.
/// <para>
/// Testable at all only since the menu became a list rather than a tree of live
/// widgets. It is the only part of the agent a user ever clicks, and the thing worth
/// pinning is that a menu built while signed out cannot offer to sign out — an item
/// that does nothing is indistinguishable, to the person clicking it, from an agent
/// that has hung.
/// </para>
/// </summary>
public sealed class TrayMenuTests
{
    private static IReadOnlyList<TrayMenuItem> Menu(AgentState state, string? account = null) =>
        TrayMenu.Build(TrayPresenter.Present(state, 1, "ADA-LAPTOP", account));

    private static TrayMenuItem Item(IReadOnlyList<TrayMenuItem> menu, TrayCommand command) =>
        menu.Single(item => item.Command == command);

    [Fact]
    public void WhoIsSignedInComesFirst()
    {
        // Before any command. It is the one fact the icon cannot show.
        Assert.Equal(
            "Signed in as Ada Lovelace (ada@example.com)",
            Menu(AgentState.Connected, "Ada Lovelace (ada@example.com)")[0].Text);
    }

    [Fact]
    public void SignedOutOffersOnlySigningIn()
    {
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.SignedOut);

        Assert.True(Item(menu, TrayCommand.SignIn).Enabled);
        Assert.False(Item(menu, TrayCommand.SignOut).Enabled);
        Assert.False(Item(menu, TrayCommand.SwitchAccount).Enabled);
    }

    [Fact]
    public void SignedInOffersLeavingButNotArrivingAgain()
    {
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.Connected, "ada@example.com");

        Assert.False(Item(menu, TrayCommand.SignIn).Enabled);
        Assert.True(Item(menu, TrayCommand.SignOut).Enabled);
        Assert.True(Item(menu, TrayCommand.SwitchAccount).Enabled);
    }

    [Fact]
    public void ReconnectingStillLetsTheUserLeaveTheAccount()
    {
        // Keyed off the account, not the connection. A machine on a train is
        // reconnecting, and its user must still be able to sign out.
        Assert.True(Item(Menu(AgentState.Reconnecting, "ada@example.com"), TrayCommand.SignOut).Enabled);
    }

    [Theory]
    [InlineData(AgentState.SignedOut)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.Connected)]
    public void TheThingsThatAlwaysWorkAlwaysWork(AgentState state)
    {
        // None of these depend on being signed in, and a disconnected agent is exactly
        // when someone needs its logs or wants to complain about it.
        IReadOnlyList<TrayMenuItem> menu = Menu(state);

        Assert.True(Item(menu, TrayCommand.ShowSessions).Enabled);
        Assert.True(Item(menu, TrayCommand.OpenLogs).Enabled);
        Assert.True(Item(menu, TrayCommand.SendFeedback).Enabled);
        Assert.True(Item(menu, TrayCommand.Quit).Enabled);
    }

    [Fact]
    public void TheVersionIsOnTheMenuAndIsNotClickable()
    {
        TrayMenuItem version = Menu(AgentState.Connected, "ada@example.com")
            .Single(item => item.Text.StartsWith("Version ", StringComparison.Ordinal));

        Assert.Equal($"Version {ProductVersion.Current}", version.Text);

        // A label, not a command: clicking it must not look like it did something.
        Assert.Equal(TrayCommand.None, version.Command);
        Assert.False(version.Enabled);
    }

    [Fact]
    public void EveryCommandAppearsExactlyOnce()
    {
        // The renderer dispatches by position, so a duplicated command would be a
        // menu where one of two identical-looking items silently does nothing.
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.Connected, "ada@example.com");

        TrayCommand[] commands = [.. menu.Where(item => item.Command != TrayCommand.None).Select(item => item.Command)];

        Assert.Equal(commands.Length, commands.Distinct().Count());
        Assert.Equal(Enum.GetValues<TrayCommand>().Length - 1, commands.Length);
    }

    [Fact]
    public void NothingClickableIsBlank()
    {
        // Separators are the only empty items; the renderer tells them apart by text
        // alone, so a command with no label would silently become one.
        foreach (TrayMenuItem item in Menu(AgentState.Connected, "ada@example.com"))
        {
            Assert.True(item.Command == TrayCommand.None || item.Text.Length > 0);
        }
    }

    [Fact]
    public void TheMenuDoesNotOpenOrCloseWithASeparator()
    {
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.Connected, "ada@example.com");

        Assert.False(menu[0].IsSeparator);
        Assert.False(menu[^1].IsSeparator);
    }
}
