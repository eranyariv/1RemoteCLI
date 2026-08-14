using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Auth;

namespace OneRemoteCli.E2E.Host;

/// <summary>
/// The people the end-to-end tests can sign in as.
/// <para>
/// Two of them, because half of what the suite has to show is that the second one sees
/// none of the first one's machines. Fixed ids rather than generated, so a failing test
/// names a person rather than a GUID.
/// </para>
/// </summary>
internal static class TestUsers
{
    public const string Scope = "Session.Access";

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    /// <summary>Whoever is running the agent. Every machine belongs to her.</summary>
    public static readonly TestUser Alice =
        new("alice", TenantA, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "alice@example.com");

    /// <summary>Allowed on the hub, and entitled to see nothing of Alice's.</summary>
    public static readonly TestUser Bob =
        new("bob", TenantB, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "bob@example.com");

    public static readonly TestUser[] All = [Alice, Bob];

    public static TestUser? Find(string? name) =>
        All.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record TestUser(string Name, string TenantId, string ObjectId, string Username)
{
    public string Key => $"{TenantId}:{ObjectId}";

    public ClaimsPrincipal Principal(string scope = TestUsers.Scope) =>
        new(new ClaimsIdentity(
            [
                new Claim(UserKey.TenantIdClaim, TenantId),
                new Claim(UserKey.ObjectIdClaim, ObjectId),
                new Claim(UserKey.PreferredUsernameClaim, Username),
                new Claim(UserKey.ScopeClaim, scope),
            ],
            NameTokenHandler.SchemeName));
}

/// <summary>
/// Admits a connection on the strength of a name.
/// <para>
/// The browser under test cannot obtain a real Entra token — automating that would put
/// a live credential in CI and make the suite depend on an identity provider being up —
/// so it presents the string <c>alice</c> or <c>bob</c> and this turns it into claims.
/// </para>
/// <para>
/// Everything downstream is production code. The claims go through the real
/// <see cref="AccountAllowlist"/>, the real <see cref="UserKey"/> derivation and the
/// real hub, so the isolation the suite asserts is the isolation that ships. Only the
/// signature check is absent, and that has its own tests in <c>Hub.Tests</c> where it
/// can be tested properly rather than through a browser.
/// </para>
/// <para>
/// This lives in a test host that is never published, and the hub it configures binds
/// to loopback on a port a test chose. It cannot be switched on in a deployment,
/// because it is not in one.
/// </para>
/// </summary>
internal sealed class NameTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccountAllowlist allowlist,
    IOptions<EntraOptions> entra) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "E2EName";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();

        // Both places a token can arrive, because a browser cannot set headers on a
        // WebSocket handshake and SignalR falls back to the query string. A handler
        // that only read the header would work under long polling and fail under the
        // transport the app actually negotiates.
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : Request.Query["access_token"].FirstOrDefault();

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        TestUser? user = TestUsers.Find(token);

        if (user is null)
        {
            return Task.FromResult(AuthenticateResult.Fail($"No test user named '{token}'."));
        }

        ClaimsPrincipal principal = user.Principal();
        AccessResult result = allowlist.Check(principal, entra.Value.RequiredScope);

        return Task.FromResult(result.IsAllowed
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
            : AuthenticateResult.Fail(result.Reason ?? "Refused."));
    }
}

/// <summary>The refresh path's half of the same substitution.</summary>
internal sealed class NameTokenValidator(AccountAllowlist allowlist, string requiredScope) : IAccessTokenValidator
{
    public Task<TokenReview> ReviewAsync(string token, CancellationToken cancellationToken = default)
    {
        TestUser? user = TestUsers.Find(token);

        if (user is null)
        {
            return Task.FromResult(TokenReview.Rejected("Unknown test user."));
        }

        ClaimsPrincipal principal = user.Principal();
        AccessResult result = allowlist.Check(principal, requiredScope);

        return Task.FromResult(result.IsAllowed
            ? TokenReview.Accepted(result.Key!, TokenExpiry.Of(principal))
            : TokenReview.Rejected(result.Reason ?? "Refused."));
    }
}
