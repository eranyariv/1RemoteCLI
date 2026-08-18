using System.Security.Claims;
using Microsoft.Identity.Client;
using OneRemoteCli.Daemon.Auth;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Naming the signed-in account.
/// <para>
/// The rule that matters is not "show the name" but "survive not having one": the
/// agent's normal start is from a cache that may predate this feature, or may be read
/// with no network at all, and an identity line that breaks in that case would break
/// on exactly the machines nobody is watching.
/// </para>
/// </summary>
public sealed class AccountNameTests
{
    [Fact]
    public void ShowsBothHalvesWhenTheNameIsKnown()
    {
        // A UPN alone does not say whose account it is, which is the entire question
        // the tray's first line exists to answer.
        Assert.Equal(
            "Eran Yariv (owner@example.com)",
            AccountName.Describe("Eran Yariv", "owner@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToTheEmailAloneRatherThanAPlaceholder(string? name)
    {
        // Not "Unknown (eran@example.com)". The email on its own is a complete answer;
        // a placeholder only advertises that something failed.
        Assert.Equal("eran@example.com", AccountName.Describe(name, "eran@example.com"));
    }

    [Fact]
    public void DoesNotSayTheSameThingTwice()
    {
        // Plenty of directories set the display name to the UPN. Repeating it reads as
        // a bug in the agent rather than a quirk of the directory.
        Assert.Equal(
            "eran@example.com",
            AccountName.Describe("eran@example.com", "eran@example.com"));

        Assert.Equal(
            "eran@example.com",
            AccountName.Describe("ERAN@EXAMPLE.COM", "eran@example.com"));
    }

    [Fact]
    public void TrimsANameTheDirectoryPaddedOut()
    {
        Assert.Equal("Ada Lovelace (ada@example.com)", AccountName.Describe("  Ada Lovelace \t", "ada@example.com"));
    }

    [Fact]
    public void ReadsTheNameClaimFromAFreshToken()
    {
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "Ada Lovelace")]));

        Assert.Equal("Ada Lovelace", AccountName.Of(claims));
    }

    [Fact]
    public void ATokenWithoutANameClaimIsNotAFailure()
    {
        // Personal accounts and some app registrations simply do not carry it.
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "ada@example.com")]));

        Assert.Null(AccountName.Of(claims));
        Assert.Null(AccountName.Of((ClaimsPrincipal?)null));
    }

    [Fact]
    public void AnAccountMsalCannotDescribeIsNotAFailureEither()
    {
        // MSAL only surfaces tenant profiles for accounts it minted itself. Anything
        // else has to come back null rather than throw, because this runs inside
        // GetAccountAsync -- on the path that decides whether the agent is signed in at
        // all -- and a decorative lookup must not be able to answer "nobody".
        Assert.Null(AccountName.Of(new ForeignAccount()));
        Assert.Null(AccountName.Of((IAccount?)null));
    }

    [Fact]
    public void TheAccountDescribesItselfSoNothingHasToDoItByHand()
    {
        // The tray, the log and `1remote status` all read this one property, which is
        // what stops them describing the same account three different ways.
        Assert.Equal(
            "Ada Lovelace (ada@example.com)",
            new SignedInAccount("uid.utid", "ada@example.com", "Ada Lovelace").Description);

        Assert.Equal("ada@example.com", new SignedInAccount("uid.utid", "ada@example.com").Description);
    }

    /// <summary>An <see cref="IAccount"/> that did not come out of MSAL's cache.</summary>
    private sealed class ForeignAccount : IAccount
    {
        public string Username => "ada@example.com";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId => new("uid.utid", "uid", "utid");
    }
}
