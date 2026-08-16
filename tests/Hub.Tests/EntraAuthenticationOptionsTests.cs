using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Auth;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// Pins the bearer options the hub actually runs with, as opposed to the validation
/// rules — which are already covered — because the setting that broke every sign-in
/// lived here and not in any of them.
/// </summary>
public class EntraAuthenticationOptionsTests
{
    /// <summary>
    /// Claim mapping must stay off.
    /// <para>
    /// With it on, the middleware renames <c>tid</c>, <c>oid</c> and <c>scp</c> to
    /// SOAP-era URIs before the hub ever sees them, and every lookup by the name the
    /// token uses returns nothing. The hub then reports each token as missing its tid
    /// and oid and refuses it — so the failure is total, and it looks like an allowlist
    /// problem rather than a configuration one.
    /// </para>
    /// <para>
    /// <see cref="UserKey"/> also understands the mapped names, so this is belt and
    /// braces. Both are deliberate: a hub that admits nobody is only obvious once
    /// somebody tries to sign in, which on a relay may be days after deployment.
    /// </para>
    /// </summary>
    [Fact]
    public void KeepsTheClaimNamesTheTokenActuallyUses()
    {
        Assert.False(BearerOptions().MapInboundClaims);
    }

    /// <summary>
    /// The name claim has to be one the token still carries after the above, or the
    /// admission log records every account as anonymous and onboarding has nothing to
    /// copy out of it.
    /// </summary>
    [Fact]
    public void NamesPrincipalsAfterTheirPreferredUsername()
    {
        Assert.Equal(
            UserKey.PreferredUsernameClaim,
            BearerOptions().TokenValidationParameters.NameClaimType);
    }

    private static JwtBearerOptions BearerOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entra:ClientId"] = "3db435ae-5e69-483c-a044-d6e8b6262fc6",
                ["Entra:Allowlist:0"] = "someone@example.com",
            })
            .Build();

        ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddEntraAuthentication(configuration)
            .BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }
}
