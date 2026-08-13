using System.Reflection;
using System.Security.Claims;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Hub.Relay;

// Generating the VAPID keypair is a one-off setup chore, but it lives here rather
// than in a script because getting the encoding wrong — padded base64, or a
// compressed point — produces keys that are accepted everywhere and then fail on a
// phone with no diagnosable symptom.
if (args.Contains("--generate-vapid", StringComparer.Ordinal))
{
    (string publicKey, string privateKey) = VapidKeys.Generate();

    Console.WriteLine($"Push__Vapid__PublicKey={publicKey}");
    Console.WriteLine($"Push__Vapid__PrivateKey={privateKey}");
    Console.WriteLine("Push__Vapid__Subject=mailto:you@example.com");

    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEntraAuthentication(builder.Configuration);

// The routing registry is a singleton because it *is* the hub's state. This is the
// load-bearing single-instance assumption of spec §4.6: an agent connected to one
// instance would be invisible to a phone connected to another.
builder.Services.AddSingleton<RelayRegistry>();

// Per-client output queues, so the slowest phone attached to any session cannot stall
// the agent connection that feeds all of them (spec §4.4).
builder.Services.AddSingleton<OutboundLimits>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OutboundFanout>();

// SignalR authenticates once, at the handshake. Everything that keeps a live socket
// from outliving its token is here (spec §3.6).
builder.Services.AddSingleton<ConnectionTokens>();
builder.Services.AddSingleton<IAccessTokenValidator, EntraAccessTokenValidator>();
builder.Services.AddHostedService<TokenExpirySweeper>();

// Web Push (spec §6.2). Subscriptions live in memory with everything else, so a
// restart drops them until each phone opens the app again — the accepted limitation
// of the no-database design, logged at startup so it is diagnosable rather than
// mysterious.
builder.Services.Configure<VapidOptions>(builder.Configuration.GetSection(VapidOptions.Section));
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddSingleton<IPushNotifier>(services => services.GetRequiredService<PushQueue>());
builder.Services.AddHttpClient<PushServiceClient>();
builder.Services.AddSingleton<IPushSender>(services =>
{
    VapidOptions vapid = services.GetRequiredService<IOptions<VapidOptions>>().Value;

    // Unconfigured is a supported state, not a failure: nobody should have to
    // provision push secrets to work on the relay.
    return vapid.Configured
        ? ActivatorUtilities.CreateInstance<WebPushSender>(services)
        : new DisabledPushSender();
});
builder.Services.AddHostedService<PushDispatcher>();

builder.Services
    .AddSignalR(RelayLiveness.Apply)
    // MessagePack rather than JSON because terminal output is binary. JSON would
    // base64 every frame, paying a third more bytes on the one path that is hot.
    .AddMessagePackProtocol();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Deployed behind App Service, which terminates TLS and forwards over plain HTTP,
// so the app itself does no HTTPS redirection.
app.MapGet("/health", () => Results.Ok(new HealthResponse(
    Status: "ok",
    Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    UtcNow: DateTimeOffset.UtcNow)));

app.MapGet("/", () => Results.Text("1RemoteCLI hub", "text/plain"));

// The VAPID public key, which the browser needs before it can subscribe.
//
// Unauthenticated, because it is public by construction — it is handed to every
// browser that subscribes, and it authorises nothing. Served over HTTP rather than
// through the hub so the PWA can fetch it before it has a socket, and so a phone
// whose token has expired can still finish setting up notifications.
app.MapGet("/push/vapid", (IOptions<VapidOptions> options) =>
{
    VapidOptions vapid = options.Value;
    return vapid.Configured
        ? Results.Ok(new VapidKeyResponse(vapid.PublicKey))
        : Results.NotFound();
});

// Lets a signed-in user read back the identity the hub resolved for them, which is
// the fastest way to find the {tid}:{oid} that belongs in the allowlist.
app.MapGet("/whoami", [Authorize] (ClaimsPrincipal user) => Results.Ok(new WhoAmIResponse(
    UserKey: UserKey.From(user),
    Username: UserKey.PreferredUsername(user))));

// Must match EntraAuthenticationExtensions.HubPathPrefix, the only path where a token
// is accepted from the query string.
app.MapHub<RelayHub>("/hub");

if (!app.Services.GetRequiredService<IOptions<VapidOptions>>().Value.Configured)
{
    app.Logger.LogWarning(
        "Push is disabled: no VAPID keypair configured under '{Section}'. " +
        "Sessions still work; phones will not be told when one is waiting.",
        VapidOptions.Section);
}

app.Run();

internal sealed record VapidKeyResponse(string Key);

internal sealed record HealthResponse(string Status, string Version, DateTimeOffset UtcNow);

internal sealed record WhoAmIResponse(string? UserKey, string? Username);

/// <summary>Exposed so tests can host this exact application.</summary>
public partial class Program;
