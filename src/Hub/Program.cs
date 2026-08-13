using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Deployed behind App Service, which terminates TLS and forwards over plain HTTP,
// so the app itself does no HTTPS redirection.
app.MapGet("/health", () => Results.Ok(new HealthResponse(
    Status: "ok",
    Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    UtcNow: DateTimeOffset.UtcNow)));

app.MapGet("/", () => Results.Text("1RemoteCLI hub", "text/plain"));

app.Run();

internal sealed record HealthResponse(string Status, string Version, DateTimeOffset UtcNow);
