using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OneRemoteCli.Hub.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEntraAuthentication(builder.Configuration);

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

app.Run();

internal sealed record HealthResponse(string Status, string Version, DateTimeOffset UtcNow);

internal sealed record WhoAmIResponse(string? UserKey, string? Username);

/// <summary>Exposed so tests can host this exact application.</summary>
public partial class Program;
