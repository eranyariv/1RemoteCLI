using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tooltip and menu state.
/// <para>
/// This is the whole diagnostic surface for a user whose phone has stopped seeing
/// their machine, so the wording is behaviour, not decoration: it has to say which of
/// the three situations they are in and what, if anything, they should do.
/// </para>
/// </summary>
public sealed class TrayPresenterTests
{
    private const string Machine = "ADA-LAPTOP";

    [Fact]
    public void StartsWithTheAgentNameAndCurrentVersion()
    {
        string[] lines = TrayPresenter.Present(
            AgentState.Connected,
            1,
            Machine,
            version: "9.99").Tooltip.Split('\n');

        Assert.Equal("1RemoteCLI Agent v9.99", lines[0]);
    }

    [Fact]
    public void ConnectedSaysHowManySessionsThePhoneCanSee()
    {
        TrayPresentation view = TrayPresenter.Present(AgentState.Connected, 3, Machine);

        // The count is the one fact the user can check against what they know they
        // started; "connected" alone does not distinguish working from wired-up-wrong.
        Assert.Contains("3 sessions", view.Tooltip, StringComparison.Ordinal);
        Assert.Contains("connected", view.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedWithNoSessionsSaysHowToStartOne()
    {
        // The likeliest reason for looking at the tray: everything is fine, and there
        // is simply nothing shared yet. Saying "0 sessions" would read like a fault.
        TrayPresentation view = TrayPresenter.Present(AgentState.Connected, 0, Machine);

        Assert.Contains("1remote pwsh", view.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("0 sessions", view.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSessionIsNotDescribedAsOneSessions()
    {
        Assert.Contains("1 session,", TrayPresenter.Present(AgentState.Connected, 1, Machine).Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedOutSaysWhatToDoAboutIt()
    {
        TrayPresentation view = TrayPresenter.Present(AgentState.SignedOut, 2, Machine);

        Assert.Contains("Sign in", view.Tooltip, StringComparison.Ordinal);

        // The only state the user can act on from the menu.
        Assert.True(view.SignInEnabled);
    }

    [Fact]
    public void ReconnectingSaysLocalSessionsAreStillFine()
    {
        // Otherwise the honest reading of "reconnecting" is "my session is broken",
        // and the user kills a terminal that was never in trouble.
        TrayPresentation view = TrayPresenter.Present(AgentState.Reconnecting, 1, Machine);

        Assert.Contains("keep working", view.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningInAgainWhileAlreadySignedInIsOffered_As_Nothing()
    {
        // Signing in when already signed in does nothing but open a browser, so the
        // menu item is disabled rather than quietly useless. Keyed off the account and
        // not the connection: a machine on a train is reconnecting, not signed out, and
        // sending that user to a browser would not reconnect anything.
        TrayPresentation view = TrayPresenter.Present(AgentState.Reconnecting, 0, Machine, "someone@example.com");

        Assert.False(view.SignInEnabled);
    }

    [Fact]
    public void SaysWhichAccountIsSignedIn()
    {
        // The question the icon cannot answer, and the one people get wrong: a browser
        // already signed in to a work account will have supplied that one.
        TrayPresentation view = TrayPresenter.Present(AgentState.Connected, 1, Machine, "someone@example.com");

        Assert.Contains("someone@example.com", view.Account, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesTheAccountHolderWhenTheTokenSaysWhoTheyAre()
    {
        // Both halves. The email identifies the account; the name is what tells the
        // user it is the account they meant.
        TrayPresentation view = TrayPresenter.Present(
            AgentState.Connected,
            1,
            Machine,
            "Ada Lovelace (ada@example.com)");

        Assert.Equal("Signed in as Ada Lovelace (ada@example.com)", view.Account);
    }

    [Fact]
    public void FallsBackToTheEmailAloneWithoutLookingBroken()
    {
        // No name claim in the cache -- an older cache, or an account that never had
        // one. The line has to keep reading as a sentence.
        TrayPresentation view = TrayPresenter.Present(AgentState.Connected, 1, Machine, "ada@example.com");

        Assert.Equal("Signed in as ada@example.com", view.Account);
    }

    [Fact]
    public void OffersToSignOutOrSwitchOnlyWhenThereIsAnAccountToLeave()
    {
        Assert.True(TrayPresenter.Present(AgentState.Connected, 1, Machine, "someone@example.com").SignOutEnabled);
        Assert.False(TrayPresenter.Present(AgentState.SignedOut, 0, Machine).SignOutEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaysSoPlainlyWhenNobodyIsSignedIn(string? account)
    {
        TrayPresentation view = TrayPresenter.Present(AgentState.SignedOut, 0, Machine, account);

        Assert.Equal("Not signed in", view.Account);
        Assert.True(view.SignInEnabled);
        Assert.False(view.SignOutEnabled);
    }

    [Theory]
    [InlineData(AgentState.Connected)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.SignedOut)]
    public void TheBadgeFollowsTheState(AgentState state) =>
        Assert.Equal(state, TrayPresenter.Present(state, 1, Machine).Badge);

    [Fact]
    public void TheMachineNameIsShownSoTwoTraysAreTellableApart()
    {
        Assert.Contains(Machine, TrayPresenter.Present(AgentState.Connected, 1, Machine).Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyMachineNameLeavesNoEmptyBrackets(string name)
    {
        TrayPresentation view = TrayPresenter.Present(AgentState.Connected, 1, name);

        Assert.DoesNotContain("()", view.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("( )", view.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongMachineNameCannotPushTheTooltipPastWhatWindowsWillShow()
    {
        // Windows drops a tooltip over 127 characters entirely rather than truncating
        // it, so an over-long name would leave the user with no tooltip at all.
        TrayPresentation view = TrayPresenter.Present(
            AgentState.Connected,
            12,
            new string('W', 400));

        Assert.True(view.Tooltip.Length <= TrayPresenter.TooltipLimit);

        // And what survives is the part that says what is going on.
        Assert.StartsWith(
            $"1RemoteCLI Agent v{ProductVersion.Current}\nconnected",
            view.Tooltip,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentState.Connected)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.SignedOut)]
    public void EveryStateSaysWhichStateItIsBelowTheProductLine(AgentState state)
    {
        string status = TrayPresenter.Present(state, 1, Machine).Tooltip.Split('\n')[1];

        Assert.Contains(
            state switch
            {
                AgentState.Connected => "connected",
                AgentState.Reconnecting => "reconnecting",
                _ => "signed out",
            },
            status,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeStatesDoNotReadAlike()
    {
        string[] tooltips =
        [
            TrayPresenter.Present(AgentState.Connected, 1, Machine).Tooltip,
            TrayPresenter.Present(AgentState.Reconnecting, 1, Machine).Tooltip,
            TrayPresenter.Present(AgentState.SignedOut, 1, Machine).Tooltip,
        ];

        Assert.Equal(3, tooltips.Distinct(StringComparer.Ordinal).Count());
    }
}
