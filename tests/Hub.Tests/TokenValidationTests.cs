using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OneRemoteCli.Hub.Auth;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// Runs real signed tokens through the hub's real validation parameters, with the
/// signing key swapped for a local one. Everything else — issuer rule, audience,
/// lifetime, skew — is exactly what production uses, because these are the checks
/// that stand between a stranger and someone's terminal.
/// </summary>
public class TokenValidationTests
{
    private const string ClientId = "3db435ae-5e69-483c-a044-d6e8b6262fc6";
    private const string Tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ObjectId = "11111111-2222-3333-4444-555555555555";
    private const string Scope = "Session.Access";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    [Fact]
    public async Task AcceptsAWellFormedToken()
    {
        TokenValidationResult result = await ValidateAsync(Token());

        Assert.True(result.IsValid);
        Assert.Equal($"{Tenant}:{ObjectId}", UserKey.From(new ClaimsPrincipal(result.ClaimsIdentity)));
    }

    [Fact]
    public async Task AcceptsTheApiUriAudienceToo()
    {
        TokenValidationResult result = await ValidateAsync(Token(audience: $"api://{ClientId}"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task RejectsAnExpiredToken()
    {
        TokenValidationResult result = await ValidateAsync(
            Token(issuedAt: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1)));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    /// <summary>
    /// The skew allowance has to be real but small: phones drift, and an hour of
    /// slack would make revocation meaningless.
    /// </summary>
    [Fact]
    public async Task ToleratesAMinuteOfClockDrift()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), EntraTokenValidation.ClockSkew);

        TokenValidationResult justExpired = await ValidateAsync(
            Token(issuedAt: DateTime.UtcNow.AddMinutes(-5), expires: DateTime.UtcNow.AddSeconds(-20)));
        Assert.True(justExpired.IsValid);

        TokenValidationResult notYetValid = await ValidateAsync(
            Token(issuedAt: DateTime.UtcNow.AddSeconds(20), expires: DateTime.UtcNow.AddHours(1)));
        Assert.True(notYetValid.IsValid);
    }

    [Fact]
    public async Task RejectsATokenForAnotherApplication()
    {
        TokenValidationResult result = await ValidateAsync(Token(audience: "00000003-0000-0000-c000-000000000000"));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidAudienceException>(result.Exception);
    }

    /// <summary>
    /// The heart of it: an attacker who controls a tenant must not be able to mint a
    /// token whose issuer says one tenant while its tid claims another.
    /// </summary>
    [Fact]
    public async Task RejectsATokenWhoseIssuerDoesNotMatchItsOwnTenant()
    {
        TokenValidationResult result = await ValidateAsync(
            Token(issuer: "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0"));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);
    }

    [Fact]
    public async Task RejectsATokenFromSomewhereElseEntirely()
    {
        TokenValidationResult result = await ValidateAsync(Token(issuer: "https://evil.example.com/v2.0"));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);
    }

    /// <summary>A v1 issuer is rejected: the app is configured for v2 access tokens.</summary>
    [Fact]
    public async Task RejectsTheVersionOneIssuerForm()
    {
        TokenValidationResult result = await ValidateAsync(
            Token(issuer: $"https://sts.windows.net/{Tenant}/"));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);
    }

    [Fact]
    public async Task RejectsATokenWithNoTenantClaim()
    {
        TokenValidationResult result = await ValidateAsync(Token(tenantId: null));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);
    }

    /// <summary>A token signed by anyone else is worthless, however well formed.</summary>
    [Fact]
    public async Task RejectsATokenSignedByAStranger()
    {
        var strangersKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "not-ours" };
        string token = Token(signingKey: strangersKey);

        TokenValidationResult result = await ValidateAsync(token);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenSignatureKeyNotFoundException>(result.Exception);
    }

    [Fact]
    public async Task RejectsAnUnsignedToken()
    {
        var handler = new JsonWebTokenHandler();
        string unsigned = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
            Audience = ClientId,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["tid"] = Tenant, ["oid"] = ObjectId, ["scp"] = Scope },
        });

        TokenValidationResult result = await ValidateAsync(unsigned);

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// Scope is checked separately from validation, so a valid token without it is
    /// still refused — just at admission rather than at signature checking.
    /// </summary>
    [Fact]
    public async Task DistinguishesAValidTokenThatLacksTheScope()
    {
        TokenValidationResult result = await ValidateAsync(Token(scope: "User.Read"));
        Assert.True(result.IsValid);

        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        var allowlist = new AccountAllowlist([$"{Tenant}:{ObjectId}"]);

        Assert.Equal(AccessDecision.MissingScope, allowlist.Check(principal, Scope).Decision);
    }

    private static async Task<TokenValidationResult> ValidateAsync(string token)
    {
        TokenValidationParameters parameters = EntraTokenValidation.CreateParameters(new EntraOptions
        {
            ClientId = ClientId,
            RequiredScope = Scope,
        });

        // The one substitution: production fetches these from the tenant's OIDC
        // metadata, which a unit test has no business reaching over the network.
        parameters.IssuerSigningKey = SigningKey;
        parameters.IssuerSigningKeys = [SigningKey];
        parameters.ConfigurationManager = null;

        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    private static string Token(
        string? tenantId = Tenant,
        string? issuer = null,
        string audience = ClientId,
        string? scope = Scope,
        DateTime? issuedAt = null,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = ObjectId,
            ["preferred_username"] = "someone@example.com",
        };

        if (tenantId is not null)
        {
            claims["tid"] = tenantId;
        }

        if (scope is not null)
        {
            claims["scp"] = scope;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? $"https://login.microsoftonline.com/{tenantId ?? Tenant}/v2.0",
            Audience = audience,
            IssuedAt = issuedAt ?? DateTime.UtcNow.AddMinutes(-1),
            NotBefore = issuedAt ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                signingKey ?? SigningKey,
                SecurityAlgorithms.RsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// The issuer validator is also exercised directly against the legacy token type,
    /// because ASP.NET may hand it either shape depending on the handler in use.
    /// </summary>
    [Fact]
    public void ValidatesTheIssuerOfALegacyJwtSecurityToken()
    {
        var token = new JwtSecurityToken(
            issuer: $"https://login.microsoftonline.com/{Tenant}/v2.0",
            audience: ClientId,
            claims: [new Claim("tid", Tenant)],
            expires: DateTime.UtcNow.AddHours(1));

        Assert.Equal(
            token.Issuer,
            EntraTokenValidation.ValidateIssuer(token.Issuer, token, new TokenValidationParameters()));

        var mismatched = new JwtSecurityToken(
            issuer: "https://login.microsoftonline.com/other/v2.0",
            audience: ClientId,
            claims: [new Claim("tid", Tenant)],
            expires: DateTime.UtcNow.AddHours(1));

        Assert.Throws<SecurityTokenInvalidIssuerException>(
            () => EntraTokenValidation.ValidateIssuer(
                mismatched.Issuer,
                mismatched,
                new TokenValidationParameters()));
    }
}
