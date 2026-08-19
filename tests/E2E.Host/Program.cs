using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Hub;
using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Daemon.Pty;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.E2E.Host;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Ops;
using OneRemoteCli.Hub.Projects;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol.Hub;

// The whole product in one process, on one origin, driven from a browser.
//
// The Playwright suite needs a hub, an agent, a pseudoconsole and the app served over
// HTTP, all agreeing about who the user is. Starting four things and teaching a test to
// wait for each of them is a reliable source of flakes, and shipping a "test mode" in
// the real hub so the browser could sign in would be a worse idea than any test it
// could support. So this is a separate program that never gets published: it wires the
// real hub and the real agent together the way `Program.cs` does on each side, swaps
// only the signature check (see `NameTokenHandler`), and serves the built app from the
// same origin so there is no CORS story and no second port to coordinate.
//
// It also exposes a small control surface under `/e2e`. That is what lets a browser
// test say "start a session" and "close it at the desk" — the two things a phone
// deliberately cannot do, and therefore the two things the scenarios need a way to
// arrange.

int port = Arg("--port") is { } p ? int.Parse(p) : 5199;
string app = Arg("--pwa") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PWA", "dist-e2e"));
string script = Arg("--script") ?? Path.Combine(AppContext.BaseDirectory, "1remote-e2e-script.exe");

if (!Directory.Exists(app))
{
    Console.Error.WriteLine($"The built app is not at {app}. Run `npm run build:e2e` in src/PWA first.");
    return 2;
}

if (!File.Exists(script))
{
    Console.Error.WriteLine($"The scripted CLI is not at {script}.");
    return 2;
}

using var stopping = new CancellationTokenSource();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.Configure<EntraOptions>(options =>
{
    options.RequiredScope = TestUsers.Scope;
    options.Allowlist = [.. TestUsers.All.Select(u => u.Key)];
});

builder.Services.AddSingleton(sp =>
    new AccountAllowlist(sp.GetRequiredService<IOptions<EntraOptions>>().Value.Allowlist));

builder.Services
    .AddAuthentication(NameTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, NameTokenHandler>(NameTokenHandler.SchemeName, _ => { });

builder.Services.AddAuthorization();

// The real registry and the real hub, wired the way src/Hub/Program.cs wires them.
builder.Services.AddSingleton<RelayRegistry>();
builder.Services.AddSingleton<OutboundLimits>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OutboundFanout>();
builder.Services.AddSingleton<ConnectionTokens>();
builder.Services.AddSingleton<IAccessTokenValidator>(sp => new NameTokenValidator(
    sp.GetRequiredService<AccountAllowlist>(),
    sp.GetRequiredService<IOptions<EntraOptions>>().Value.RequiredScope));
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<IPushNotifier, DroppingNotifier>();

// The relay counts usage for the operator channel, so the hub cannot be constructed
// without a recorder. An unconfigured deployment gets the one that counts nothing,
// which is exactly what the real hub does when no Telegram chat is configured; what
// the channel reports is the hub tests' business, not the browser's.
builder.Services.AddSingleton<IUsageRecorder, NullUsageRecorder>();

// Projects (issue #110). RelayHub takes a ProjectStore constructor dependency like
// every other piece of hub state, so it has to be wired here too - scoped to a
// scratch file this process owns, torn down when the process exits, since nothing
// about the browser suite needs projects to survive between runs.
string projectStatePath = Path.Combine(Path.GetTempPath(), $"1remote-e2e-projects-{Guid.NewGuid():n}.json");
string projectIconRoot = Path.Combine(Path.GetTempPath(), $"1remote-e2e-icons-{Guid.NewGuid():n}");
builder.Services.Configure<ProjectsOptions>(options =>
{
    options.StatePath = projectStatePath;
    options.IconRoot = projectIconRoot;
});
builder.Services.AddSingleton<ProjectStore>();

builder.Services.AddSignalR(RelayLiveness.Apply).AddMessagePackProtocol();

WebApplication host = builder.Build();

host.UseAuthentication();
host.UseAuthorization();
host.MapHub<RelayHub>("/hub");

// ---------------------------------------------------------------------------
// The agent, in this process, against the hub above.
// ---------------------------------------------------------------------------

string identityPath = Path.Combine(Path.GetTempPath(), $"1remote-e2e-{Guid.NewGuid():n}.json");
var identity = new MachineIdentity(Guid.NewGuid().ToString("n"), "desk");
identity.Save(identityPath);

var sessions = new SessionRegistry();
var hubUri = new Uri($"http://127.0.0.1:{port}/hub");

using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());

// Signed in as Alice, always. The agent is her machine; Bob's presence in the suite is
// to prove he cannot see it, not to give him one of his own.
var agent = new AgentHubClient(
    hubUri,
    identity,
    sessions,
    _ => Task.FromResult<string?>(TestUsers.Alice.Name),
    loggers.CreateLogger("agent"));

var pipe = new AgentPipeServer($"1remote-e2e-{Guid.NewGuid():n}");
var agentHost = new AgentHost(identity, sessions, agent, pipe);

var shells = new ConcurrentDictionary<string, Shell>();

// ---------------------------------------------------------------------------
// The control surface. Everything a phone cannot do, and the scenarios need done.
// ---------------------------------------------------------------------------

host.MapGet("/e2e/ready", () => Results.Json(new
{
    connected = agent.IsConnected,
    machineId = identity.MachineId,
    sessions = sessions.Snapshot().Count,
}));

host.MapPost("/e2e/sessions", async (string? name) =>
{
    Shell shell = await StartShellAsync(name).ConfigureAwait(false);
    return Results.Json(new { sessionId = shell.SessionId, machineId = identity.MachineId });
});

// The size the *pseudoconsole* believes it is, which is the only number a scenario
// about resizing can trust: the browser's own figure is a measurement of a font, and
// the program's figure is a measurement of what it was told.
host.MapGet("/e2e/sessions/{id}/size", (string id) =>
    shells.TryGetValue(id, out Shell? shell)
        ? Results.Json(new { cols = shell.Pty.Cols, rows = shell.Pty.Rows })
        : Results.NotFound());

// Ends a session the way the desk would: the program goes away, and everything the
// phone is told about it afterwards is the agent's own doing.
host.MapDelete("/e2e/sessions/{id}", async (string id) =>
{
    if (!shells.TryRemove(id, out Shell? shell))
    {
        return Results.NotFound();
    }

    await shell.DisposeAsync().ConfigureAwait(false);
    return Results.Ok();
});

// ---------------------------------------------------------------------------
// The app itself, from the same origin, so the browser has one place to talk to.
// ---------------------------------------------------------------------------

var files = new PhysicalFileProvider(app);

host.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
host.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    // The suite rebuilds the app between runs and a cached index.html would quietly
    // pin a browser to the previous build's asset hashes.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
});

// A single-page app: any path the static files did not answer is the app's own route.
host.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });

await host.StartAsync().ConfigureAwait(false);

Task running = Task.WhenAll(
    agentHost.RunAsync(stopping.Token),
    agent.RunAsync(stopping.Token));

await WaitForAsync(() => agent.IsConnected, "the agent to reach the hub").ConfigureAwait(false);

// The line the test harness waits for. Printed only once everything is actually up, so
// that a test which starts on seeing it cannot race the agent's registration.
Console.WriteLine($"E2E-HOST-READY http://127.0.0.1:{port}/ machine={identity.MachineId}");
Console.Out.Flush();

await host.WaitForShutdownAsync().ConfigureAwait(false);

await stopping.CancelAsync().ConfigureAwait(false);

foreach (Shell shell in shells.Values)
{
    await shell.DisposeAsync().ConfigureAwait(false);
}

await Quietly(() => running).ConfigureAwait(false);
File.Delete(identityPath);

try
{
    File.Delete(projectStatePath);
}
catch (IOException)
{
}

try
{
    if (Directory.Exists(projectIconRoot))
    {
        Directory.Delete(projectIconRoot, recursive: true);
    }
}
catch (IOException)
{
}

return 0;

string? Arg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task<Shell> StartShellAsync(string? displayName)
{
    var desk = new HeadlessTerminal(cols: 80, rows: 24);

    PseudoConsoleSession pty = PseudoConsoleSession.Start(
        script,
        Path.GetTempPath(),
        desk.Cols,
        desk.Rows);

    AgentPipeClient client = await AgentPipeClient
        .ConnectAsync(pipe.PipeName, cancellationToken: stopping.Token)
        .ConfigureAwait(false);

    string sessionId = await client.OpenSessionAsync(
        new SessionStartInfo(script, [], Path.GetTempPath(), desk.Cols, desk.Rows, displayName ?? "e2e script"),
        stopping.Token).ConfigureAwait(false);

    var wrapper = new WrapperSession(pty, desk, client, _ => { });
    Task run = wrapper.RunAsync(stopping.Token);

    var shell = new Shell(sessionId, pty, client, desk, run);
    shells[sessionId] = shell;

    return shell;
}

static async Task WaitForAsync(Func<bool> condition, string what)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(30);

    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(50).ConfigureAwait(false);
    }

    throw new TimeoutException($"Timed out waiting for {what}.");
}

static async Task Quietly(Func<Task> action)
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[e2e-host] {ex.Message}");
    }
}

namespace OneRemoteCli.E2E.Host
{
    /// <summary>One scripted CLI, wrapped and attached to the agent.</summary>
    internal sealed record Shell(
        string SessionId,
        PseudoConsoleSession Pty,
        AgentPipeClient Pipe,
        HeadlessTerminal Desk,
        Task Run)
    {
        public async ValueTask DisposeAsync()
        {
            await Pty.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAny(Run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            await Pipe.DisposeAsync().ConfigureAwait(false);
            Desk.Dispose();
        }
    }

    /// <summary>
    /// The push queue, unread.
    /// <para>
    /// Which is exactly what a hub with no VAPID keys does in production. Notifications
    /// have their own tests; a browser test that waited on a real push service would be
    /// testing Apple's infrastructure.
    /// </para>
    /// </summary>
    internal sealed class DroppingNotifier : IPushNotifier
    {
        public void Enqueue(string userKey, PushPayload payload)
        {
        }
    }
}
