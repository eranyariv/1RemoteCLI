using System.Security.Claims;
using OneRemoteCli.Hub.Auth;

namespace OneRemoteCli.Hub.Tests;

public class AccountAllowlistTests
{
    private const string Tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ObjectId = "11111111-2222-3333-4444-555555555555";
    private const string Key = $"{Tenant}:{ObjectId}";

    [Fact]
    public void AdmitsAnAccountListedByUserKey()
    {
        AccessResult result = new AccountAllowlist([Key]).Check(Principal(), "Session.Access");

        Assert.True(result.IsAllowed);
        Assert.Equal(Key, result.Key);
    }

    /// <summary>
    /// Listing by email exists so someone can be onboarded before anyone knows their
    /// oid. It is the weaker form — usernames get reassigned — but it is bounded by
    /// still requiring a validated token.
    /// </summary>
    [Fact]
    public void AdmitsAnAccountListedByUsername()
    {
        Assert.True(new AccountAllowlist(["someone@example.com"]).Check(Principal(), "Session.Access").IsAllowed);
    }

    [Fact]
    public void MatchesUsernamesWithoutCaringAboutCase()
    {
        Assert.True(new AccountAllowlist(["SomeOne@Example.COM"]).Check(Principal(), "Session.Access").IsAllowed);
    }

    [Fact]
    public void RefusesAValidIdentityThatIsNotListed()
    {
        AccessResult result = new AccountAllowlist(["other@example.com"]).Check(Principal(), "Session.Access");

        Assert.Equal(AccessDecision.NotAllowlisted, result.Decision);
        Assert.Contains(Key, result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty list means nobody, not everybody. A hub that accidentally admits the
    /// world is a much worse failure than one nobody can reach — and the second kind
    /// gets noticed immediately.
    /// </summary>
    [Fact]
    public void RefusesEveryoneWhenNobodyIsListed()
    {
        var allowlist = new AccountAllowlist([]);

        Assert.True(allowlist.IsEmpty);
        Assert.Equal(AccessDecision.NotAllowlisted, allowlist.Check(Principal(), "Session.Access").Decision);
    }

    [Fact]
    public void RefusesATokenWithoutTheRequiredScope()
    {
        AccessResult result = new AccountAllowlist([Key]).Check(Principal(scope: "User.Read"), "Session.Access");

        Assert.Equal(AccessDecision.MissingScope, result.Decision);
    }

    [Fact]
    public void RefusesATokenWithNoUserKey()
    {
        AccessResult result = new AccountAllowlist([Key]).Check(Principal(objectId: null), "Session.Access");

        Assert.Equal(AccessDecision.NoUserKey, result.Decision);
    }

    /// <summary>Each refusal has to be distinguishable, or nobody can debug an outage.</summary>
    [Fact]
    public void ExplainsEachRefusalDifferently()
    {
        var allowlist = new AccountAllowlist([Key]);

        string[] reasons =
        [
            allowlist.Check(Principal(objectId: null), "Session.Access").Reason,
            allowlist.Check(Principal(scope: "User.Read"), "Session.Access").Reason,
            new AccountAllowlist([]).Check(Principal(), "Session.Access").Reason,
        ];

        Assert.Equal(3, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IgnoresBlankEntries()
    {
        var allowlist = new AccountAllowlist(["", "   ", Key]);

        Assert.Equal(1, allowlist.Count);
        Assert.True(allowlist.Check(Principal(), "Session.Access").IsAllowed);
    }

    [Fact]
    public void TrimsEntriesCopiedOutOfLogs()
    {
        Assert.True(new AccountAllowlist([$"  {Key}  "]).Check(Principal(), "Session.Access").IsAllowed);
    }

    private static ClaimsPrincipal Principal(
        string? tenantId = Tenant,
        string? objectId = ObjectId,
        string? scope = "Session.Access",
        string? username = "someone@example.com")
    {
        List<Claim> claims = [];

        if (tenantId is not null)
        {
            claims.Add(new Claim("tid", tenantId));
        }

        if (objectId is not null)
        {
            claims.Add(new Claim("oid", objectId));
        }

        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        if (username is not null)
        {
            claims.Add(new Claim("preferred_username", username));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}

public class UserKeyTests
{
    [Fact]
    public void CombinesTenantAndObjectId()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", "t"), new Claim("oid", "o")],
            "Test"));

        Assert.Equal("t:o", UserKey.From(principal));
    }

    /// <summary>
    /// oid alone is not enough for a multi-tenant app: object ids are unique within
    /// a tenant, and this app accepts every tenant.
    /// </summary>
    [Fact]
    public void DistinguishesTheSameObjectIdInDifferentTenants()
    {
        static ClaimsPrincipal In(string tenant) => new(new ClaimsIdentity(
            [new Claim("tid", tenant), new Claim("oid", "shared")],
            "Test"));

        Assert.NotEqual(UserKey.From(In("tenant-a")), UserKey.From(In("tenant-b")));
    }

    [Theory]
    [InlineData("tid")]
    [InlineData("oid")]
    public void RefusesToGuessWhenAClaimIsMissing(string present)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(present, "x")], "Test"));

        Assert.Null(UserKey.From(principal));
    }

    /// <summary>ASP.NET sometimes maps these claims to their long URI forms.</summary>
    [Fact]
    public void ReadsTheLongClaimUris()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("http://schemas.microsoft.com/identity/claims/tid", "t"),
                new Claim("http://schemas.microsoft.com/identity/claims/oid", "o"),
            ],
            "Test"));

        Assert.Equal("t:o", UserKey.From(principal));
    }

    /// <summary>
    /// scp is one space-delimited string. A substring test would admit a scope that
    /// merely starts with ours.
    /// </summary>
    [Fact]
    public void MatchesWholeScopesOnly()
    {
        static ClaimsPrincipal With(string scp) => new(new ClaimsIdentity([new Claim("scp", scp)], "Test"));

        Assert.True(UserKey.HasScope(With("Session.Access"), "Session.Access"));
        Assert.True(UserKey.HasScope(With("User.Read Session.Access offline_access"), "Session.Access"));
        Assert.False(UserKey.HasScope(With("Session.AccessAll"), "Session.Access"));
        Assert.False(UserKey.HasScope(With("session.access"), "Session.Access"));
        Assert.False(UserKey.HasScope(new ClaimsPrincipal(new ClaimsIdentity()), "Session.Access"));
    }
}

public class EntraOptionsTests
{
    [Fact]
    public void AcceptsBothAudienceFormsForTheSameApplication()
    {
        var options = new EntraOptions { ClientId = "abc" };

        Assert.Equal(["abc", "api://abc"], options.ValidAudiences());
    }

    [Fact]
    public void RequiresSessionAccessByDefault()
    {
        Assert.Equal("Session.Access", new EntraOptions().RequiredScope);
    }
}
