using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Relay;

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

// Lets a signed-in user read back the identity the hub resolved for them, which is
// the fastest way to find the {tid}:{oid} that belongs in the allowlist.
app.MapGet("/whoami", [Authorize] (ClaimsPrincipal user) => Results.Ok(new WhoAmIResponse(
    UserKey: UserKey.From(user),
    Username: UserKey.PreferredUsername(user))));

// Must match EntraAuthenticationExtensions.HubPathPrefix, the only path where a token
// is accepted from the query string.
app.MapHub<RelayHub>("/hub");

app.Run();

internal sealed record HealthResponse(string Status, string Version, DateTimeOffset UtcNow);

internal sealed record WhoAmIResponse(string? UserKey, string? Username);

/// <summary>Exposed so tests can host this exact application.</summary>
public partial class Program;
