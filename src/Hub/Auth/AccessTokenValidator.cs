using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace OneRemoteCli.Hub.Auth;

/// <summary>What the hub learned from a token presented on a live connection.</summary>
public sealed record TokenReview(bool IsValid, string? UserKey, DateTimeOffset? ExpiresAt, string? Reason)
{
    public static TokenReview Rejected(string reason) => new(false, null, null, reason);

    public static TokenReview Accepted(string userKey, DateTimeOffset? expiresAt) =>
        new(true, userKey, expiresAt, null);
}

/// <summary>
/// Checks a token that arrives <em>after</em> the handshake.
/// <para>
/// An interface because the check has to be the real one in production and a
/// substitutable one in tests: the end-to-end harness deliberately does not sign real
/// JWTs, and a refresh path that could only be exercised against Entra would be a
/// refresh path that is never exercised.
/// </para>
/// </summary>
public interface IAccessTokenValidator
{
    Task<TokenReview> ReviewAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production check: exactly what the handshake does, run again.
/// <para>
/// "Exactly" is the requirement, not an aspiration. A refresh path that validated less
/// than the handshake would be a way to launder a token past the checks — present a
/// weak token on a connection whose first token was strong, and the connection carries
/// on. So this reuses the same <see cref="TokenValidationParameters"/> the bearer
/// handler was configured with, the same signing keys from the same metadata, and the
/// same allowlist.
/// </para>
/// </summary>
public sealed class EntraAccessTokenValidator(
    IOptionsMonitor<JwtBearerOptions> bearerOptions,
    IOptions<EntraOptions> entraOptions,
    AccountAllowlist allowlist) : IAccessTokenValidator
{
    private readonly IOptionsMonitor<JwtBearerOptions> _bearerOptions =
        bearerOptions ?? throw new ArgumentNullException(nameof(bearerOptions));

    private readonly IOptions<EntraOptions> _entraOptions =
        entraOptions ?? throw new ArgumentNullException(nameof(entraOptions));

    private readonly AccountAllowlist _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<TokenReview> ReviewAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenReview.Rejected("The refreshed token is empty.");
        }

        JwtBearerOptions options = _bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
        TokenValidationParameters parameters = options.TokenValidationParameters.Clone();

        // The signing keys live behind the same metadata document the handler uses, and
        // are refetched by the configuration manager when they roll. Fetching them here
        // rather than caching a copy is what keeps a key rollover from turning every
        // refresh into a disconnection.
        if (options.ConfigurationManager is { } manager)
        {
            OpenIdConnectConfiguration configuration =
                await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

            parameters.IssuerSigningKeys = configuration.SigningKeys;
        }

        TokenValidationResult result;

        try
        {
            result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A malformed token throws rather than returning a failed result, and a
            // throw out of a hub method reaches the caller as an opaque string.
            return TokenReview.Rejected(error.Message);
        }

        if (!result.IsValid)
        {
            return TokenReview.Rejected(result.Exception?.Message ?? "The refreshed token is not valid.");
        }

        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        AccessResult access = _allowlist.Check(principal, _entraOptions.Value.RequiredScope);

        // Checked on every refresh, not only at the handshake. Removing somebody from
        // the allowlist has to end their access, and a connection that could refresh
        // its way past the list would outlive the decision to remove them.
        return access.IsAllowed
            ? TokenReview.Accepted(access.Key!, TokenExpiry.Of(principal))
            : TokenReview.Rejected(access.Reason ?? "This account is not allowed on this hub.");
    }
}
