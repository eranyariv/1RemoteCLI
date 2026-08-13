using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// Stands in for Entra so the tests can be two different real users without a
/// tenant. The claims it produces are exactly the ones a v2.0 access token carries
/// and the hub reads, so the code under test cannot tell the difference.
/// </summary>
internal sealed class HeaderIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestIdentity";
    public const string TenantHeader = "X-Test-Tid";
    public const string ObjectHeader = "X-Test-Oid";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TenantHeader, out var tenantId) ||
            !Request.Headers.TryGetValue(ObjectHeader, out var objectId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("tid", tenantId.ToString()),
                new Claim("oid", objectId.ToString()),
                new Claim("scp", "Session.Access"),
                new Claim("preferred_username", $"{objectId}@example.test"),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
