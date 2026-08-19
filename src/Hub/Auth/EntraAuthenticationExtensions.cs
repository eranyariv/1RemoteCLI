using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Auth;

/// <summary>Wires Entra authentication and the allowlist into the hub.</summary>
public static class EntraAuthenticationExtensions
{
    /// <summary>The authority for the OIDC metadata and signing keys.</summary>
    private const string CommonAuthority = "https://login.microsoftonline.com/common/v2.0";

    /// <summary>Paths where a token may arrive as a query parameter. See below.</summary>
    private const string HubPathPrefix = "/hub";

    /// <summary>
    /// Matches <c>/projects/{id}/icon</c> — the other place a token has to travel in
    /// the query string, for the same reason as <see cref="HubPathPrefix"/>: it is
    /// loaded by an <c>&lt;img&gt;</c> tag, which cannot attach an Authorization header
    /// either.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProjectIconPath =
        new(@"^/projects/[^/]+/icon$", System.Text.RegularExpressions.RegexOptions.Compiled);

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

                // Keep the claim names the token actually uses. Left on, the handler
                // rewrites tid, oid and scp into SOAP-era URIs, and every lookup by
                // short name silently returns nothing -- which reads as "this token
                // has no tid or oid" and refuses everyone, including the accounts on
                // the allowlist. Tests never saw it because they build principals
                // directly, where no mapping happens.
                options.MapInboundClaims = false;

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Browsers cannot set an Authorization header on a WebSocket
                        // handshake, so SignalR passes the token in the query string.
                        // Accepted only on the hub path (plus the project icon path,
                        // loaded by a plain <img> tag for the same reason), so a token
                        // cannot end up in the access log of an ordinary page request.
                        if ((context.Request.Path.StartsWithSegments(HubPathPrefix) ||
                                ProjectIconPath.IsMatch(context.Request.Path.Value ?? string.Empty)) &&
                            context.Request.Query.TryGetValue("access_token", out Microsoft.Extensions.Primitives.StringValues token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },

                    // A token that did not validate at all: wrong signature, wrong
                    // audience, expired beyond the skew. One is a clock that drifted;
                    // a run of them is a misconfiguration or somebody trying the door,
                    // and neither is otherwise discoverable until a user complains.
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<FailureRates>()
                            .TokenRejected();

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
/// Logs each account the hub sees, once — and tells the operator about the ones it
/// turned away.
/// <para>
/// Onboarding needs the resolved <c>{tid}:{oid}</c>, and nobody can look it up
/// before their first connection — so the hub prints it, including for accounts it
/// just turned away. Once, because a phone that reconnects on every tunnel change
/// would otherwise bury the log.
/// </para>
/// <para>
/// The Telegram alert hangs off the same de-duplication rather than having its own.
/// A refused phone retries on a timer, and an operator woken forty times by one
/// misconfigured account would mute the channel — which is a worse outcome than
/// never having built it.
/// </para>
/// </summary>
public sealed class AdmissionLog(IOperatorNotifier notifier)
{
    private readonly IOperatorNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
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

            return;
        }

        logger.LogWarning(
            "Refused {Username} ({UserKey}): {Reason}. Add \"{UserKey}\" to Entra:Allowlist to admit them.",
            result.Username ?? "(no username)",
            result.Key ?? "(no user key)",
            result.Reason,
            result.Key ?? result.Username ?? string.Empty);

        // The highest-value thing this channel does, and it needs no storage. Without
        // it the person sees a failure, nobody is told, and a valid user is stuck at a
        // dead end that takes one line of configuration to clear.
        //
        // Mapped to the channel's own enum rather than passing the AccessResult, whose
        // Reason is an assembled sentence. The vocabulary takes no free text, by design.
        _notifier.Send(new OperatorMessage.AccountRefused(
            result.Username ?? string.Empty,
            result.Key,
            result.Decision switch
            {
                AccessDecision.MissingScope => RefusalKind.MissingScope,
                AccessDecision.NoUserKey => RefusalKind.NoUserKey,
                _ => RefusalKind.NotAllowlisted,
            }));
    }
}
