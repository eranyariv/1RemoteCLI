using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Auth;

/// <summary>Wires Entra authentication and the allowlist into the hub.</summary>
public static class EntraAuthenticationExtensions
{
    /// <summary>The authority for the OIDC metadata and signing keys.</summary>
    private const string CommonAuthority = "https://login.microsoftonline.com/common/v2.0";

    /// <summary>Paths where a token may arrive as a query parameter. See below.</summary>
    private const string HubPathPrefix = "/hub";

    public static IServiceCollection AddEntraAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<EntraOptions>(configuration.GetSection(EntraOptions.SectionName));

        services.AddSingleton(sp =>
            new AccountAllowlist(sp.GetRequiredService<IOptions<EntraOptions>>().Value.Allowlist));

        services.AddSingleton<AdmissionLog>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                EntraOptions entra = configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>()
                    ?? new EntraOptions();

                // Metadata comes from the common endpoint, which publishes the signing
                // keys for every tenant. The issuer is still checked per token against
                // its own tid; see EntraTokenValidation.ValidateIssuer.
                options.Authority = CommonAuthority;
                options.TokenValidationParameters = EntraTokenValidation.CreateParameters(entra);

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Browsers cannot set an Authorization header on a WebSocket
                        // handshake, so SignalR passes the token in the query string.
                        // Accepted only on the hub path, so a token cannot end up in
                        // the access log of an ordinary page request.
                        if (context.Request.Path.StartsWithSegments(HubPathPrefix) &&
                            context.Request.Query.TryGetValue("access_token", out Microsoft.Extensions.Primitives.StringValues token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },

                    OnTokenValidated = context =>
                    {
                        // The token is genuine by this point. Whether this person may
                        // use *this* hub is a separate question, and it is answered
                        // here so a refusal happens during the handshake rather than
                        // after a connection is established.
                        var allowlist = context.HttpContext.RequestServices.GetRequiredService<AccountAllowlist>();
                        var log = context.HttpContext.RequestServices.GetRequiredService<AdmissionLog>();
                        EntraOptions entra = context.HttpContext.RequestServices
                            .GetRequiredService<IOptions<EntraOptions>>().Value;

                        AccessResult result = allowlist.Check(context.Principal!, entra.RequiredScope);

                        log.Record(
                            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>(),
                            result);

                        if (!result.IsAllowed)
                        {
                            context.Fail(result.Reason);
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();

        return services;
    }
}

/// <summary>
/// Logs each account the hub sees, once.
/// <para>
/// Onboarding needs the resolved <c>{tid}:{oid}</c>, and nobody can look it up
/// before their first connection — so the hub prints it, including for accounts it
/// just turned away. Once, because a phone that reconnects on every tunnel change
/// would otherwise bury the log.
/// </para>
/// </summary>
public sealed class AdmissionLog
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public void Record(ILoggerFactory loggerFactory, AccessResult result)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(result);

        string identity = result.Key ?? result.Username ?? "unknown";
        if (!_seen.TryAdd($"{identity}/{result.Decision}", 0))
        {
            return;
        }

        ILogger logger = loggerFactory.CreateLogger<AdmissionLog>();

        if (result.IsAllowed)
        {
            logger.LogInformation(
                "Admitted {Username} as {UserKey}.",
                result.Username ?? "(no username)",
                result.Key);
        }
        else
        {
            logger.LogWarning(
                "Refused {Username} ({UserKey}): {Reason}. Add \"{UserKey}\" to Entra:Allowlist to admit them.",
                result.Username ?? "(no username)",
                result.Key ?? "(no user key)",
                result.Reason,
                result.Key ?? result.Username ?? string.Empty);
        }
    }
}
