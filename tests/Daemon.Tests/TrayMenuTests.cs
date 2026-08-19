using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Daemon.Update;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tray menu's contents.
/// <para>
/// Testable at all only since the menu became a list rather than a tree of live
/// widgets. It is the only part of the agent a user ever clicks, and since the
/// settings window took over the account, the logs, feedback and the version
/// (issue #73), what is worth pinning is mostly what is <em>not</em> here: a second
/// copy of that information would eventually disagree with the first.
/// </para>
/// </summary>
public sealed class TrayMenuTests
{
    private static IReadOnlyList<TrayMenuItem> Menu(AgentState state, string? account = null) =>
        TrayMenu.Build(TrayPresenter.Present(state, 1, "ADA-LAPTOP", account));

    private static IReadOnlyList<TrayMenuItem> MenuWithUpdate(string version = "0.13") =>
        TrayMenu.Build(TrayPresenter.Present(
            AgentState.Connected,
            1,
            "ADA-LAPTOP",
            "ada@example.com",
            new UpdateStatus(UpdateStage.Available, version)));

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

    [Theory]
    [InlineData(AgentState.SignedOut)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.Connected)]
    public void EverythingOnTheMenuAlwaysWorks(AgentState state)
    {
        // Nothing left on the menu depends on being signed in, and a disconnected agent
        // is exactly when someone needs to open the window that says why.
        IReadOnlyList<TrayMenuItem> menu = Menu(state);

        Assert.True(Item(menu, TrayCommand.Settings).Enabled);
        Assert.True(Item(menu, TrayCommand.ShowSessions).Enabled);
        Assert.True(Item(menu, TrayCommand.Quit).Enabled);
    }

    [Fact]
    public void SigningInAndOutMovedIntoTheWindow()
    {
        // Two places offering to sign out is two places that can disagree about whether
        // you are signed in, and the wrong one is the one somebody reads.
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.SignedOut);

        Assert.DoesNotContain(menu, item => item.Text.Contains("Sign ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheVersionMovedIntoTheWindowToo()
    {
        Assert.DoesNotContain(
            Menu(AgentState.Connected, "ada@example.com"),
            item => item.Text.StartsWith("Version ", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactlyOneItemIsTheDefaultAndItIsWhatDoubleClickDoes()
    {
        // The bold item is a promise about what double-clicking the icon does. Two
        // defaults would leave the shell to pick which one to embolden, and none would
        // make the shortcut undiscoverable.
        TrayMenuItem theDefault = Menu(AgentState.Connected, "ada@example.com")
            .Single(item => item.IsDefault);

        Assert.Equal(TrayMenu.DefaultCommand, theDefault.Command);
        Assert.Equal(TrayCommand.Settings, theDefault.Command);
        Assert.True(theDefault.Enabled);
    }

    [Fact]
    public void TheMenuDoesNotOfferToSwitchAccounts()
    {
        // It was sign-out followed by sign-in, both of which are in the window now, so
        // it cost a permanent line for a once-ever action (issue #71).
        IReadOnlyList<TrayMenuItem> menu = Menu(AgentState.Connected, "ada@example.com");

        Assert.DoesNotContain(menu, item => item.Text.Contains("different account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryCommandAppearsExactlyOnce()
    {
        // The renderer dispatches by position, so a duplicated command would be a
        // menu where one of two identical-looking items silently does nothing.
        IReadOnlyList<TrayMenuItem> menu = MenuWithUpdate();

        TrayCommand[] commands = [.. menu.Where(item => item.Command != TrayCommand.None).Select(item => item.Command)];

        Assert.Equal(commands.Length, commands.Distinct().Count());
        Assert.Equal(Enum.GetValues<TrayCommand>().Length - 1, commands.Length);
    }

    /// <summary>
    /// The one item that is not always there. Offering "Update to 0.13" on a machine
    /// that is already on 0.13 would be an invitation to a download that decides it has
    /// nothing to do.
    /// </summary>
    [Fact]
    public void ThereIsNothingToUpdateToUntilThereIs() =>
        Assert.DoesNotContain(Menu(AgentState.Connected, "ada@example.com"), item => item.Command == TrayCommand.Update);

    [Fact]
    public void OffersTheReleaseThatWasFound() =>
        Assert.Equal("Update to 0.13", Item(MenuWithUpdate(), TrayCommand.Update).Text);

    /// <summary>
    /// Above Quit, because the two are the only items that change the process, and Quit
    /// stays last where muscle memory expects it.
    /// </summary>
    [Fact]
    public void TheUpdateSitsJustAboveQuit()
    {
        IReadOnlyList<TrayMenuItem> menu = MenuWithUpdate();

        int update = menu.ToList().FindIndex(item => item.Command == TrayCommand.Update);
        int quit = menu.ToList().FindIndex(item => item.Command == TrayCommand.Quit);

        Assert.Equal(quit - 1, update);
    }

    [Fact]
    public void TheUpdateIsSetOffFromWhatItIsNot()
    {
        // A separator above it: it is not another way to look at this machine, it
        // changes the program.
        IReadOnlyList<TrayMenuItem> menu = MenuWithUpdate();
        int update = menu.ToList().FindIndex(item => item.Command == TrayCommand.Update);

        Assert.Equal(TrayCommand.None, menu[update - 1].Command);
    }

    [Fact]
    public void TheMenuStaysShort()
    {
        // The point of the window was to stop this growing. A menu long enough to need
        // reading is one nobody reads.
        Assert.True(Menu(AgentState.Connected, "ada@example.com").Count <= 8);
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
