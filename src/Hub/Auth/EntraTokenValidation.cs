using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace OneRemoteCli.Hub.Auth;

/// <summary>
/// Builds the token validation rules the hub applies to every connection.
/// </summary>
public static class EntraTokenValidation
{
    /// <summary>
    /// A minute either side. Enough for the clock drift a phone or a laptop
    /// realistically has, and short enough that an expired token stays expired.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>The v2.0 issuer template. Only v2.0: the app requests v2 access tokens.</summary>
    private const string IssuerTemplate = "https://login.microsoftonline.com/{0}/v2.0";

    public static TokenValidationParameters CreateParameters(EntraOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = ValidateIssuer,

            ValidateAudience = true,
            ValidAudiences = options.ValidAudiences(),

            ValidateLifetime = true,
            ClockSkew = ClockSkew,

            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,

            NameClaimType = UserKey.PreferredUsernameClaim,
        };
    }

    /// <summary>
    /// Checks the issuer against the token's own tenant id.
    /// <para>
    /// This is the subtle one. An app that signs in against <c>common</c> receives
    /// tokens issued by whichever tenant the user belongs to, so there is no single
    /// correct issuer string to compare against. A static value is wrong in both
    /// directions: pin it to one tenant and every other legitimate user is rejected;
    /// leave it off and the hub accepts tokens whose issuer never had to match the
    /// tenant they claim to come from. The only sound rule is that the issuer must
    /// be exactly the one belonging to the <c>tid</c> inside the token.
    /// </para>
    /// </summary>
    public static string ValidateIssuer(
        string issuer,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        string? tenantId = TenantIdOf(securityToken);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new SecurityTokenInvalidIssuerException("The token has no tid claim.")
            {
                InvalidIssuer = issuer,
            };
        }

        string expected = string.Format(System.Globalization.CultureInfo.InvariantCulture, IssuerTemplate, tenantId);

        if (!string.Equals(issuer, expected, StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidIssuerException(
                $"Issuer '{issuer}' does not match the token's own tenant '{tenantId}'.")
            {
                InvalidIssuer = issuer,
            };
        }

        return issuer;
    }

    private static string? TenantIdOf(SecurityToken securityToken) => securityToken switch
    {
        JsonWebToken jwt => jwt.TryGetClaim(UserKey.TenantIdClaim, out System.Security.Claims.Claim? claim)
            ? claim.Value
            : null,
        System.IdentityModel.Tokens.Jwt.JwtSecurityToken legacy =>
            legacy.Claims.FirstOrDefault(c => c.Type == UserKey.TenantIdClaim)?.Value,
        _ => null,
    };
}
