using System.Security.Claims;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Ops;
using OneRemoteCli.Hub.Projects;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Hub.Speech;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

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

// Azure AI Speech for voice mode (issue #168). The browser receives only a
// short-lived Speech token; the resource key stays in App Service configuration or
// behind a Key Vault reference. Token grants are rate-limited per signed-in identity
// because they are the point where a user gains access to the metered Azure resource.
builder.Services.Configure<AzureSpeechOptions>(
    builder.Configuration.GetSection(AzureSpeechOptions.Section));
builder.Services.AddHttpClient(AzureSpeechTokenBroker.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
}).RemoveAllLoggers();
builder.Services.AddSingleton<ISpeechTokenBroker, AzureSpeechTokenBroker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("voice-token", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            UserKey.From(context.User) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

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

// Fan-out of an operator broadcast to every subscribed phone. A separate interface
// from IPushNotifier so that nothing in Ops ever names a PushPayload — see
// OperatorMessage for why that boundary is drawn with a type rather than a comment.
builder.Services.AddSingleton<IPushBroadcaster, PushBroadcaster>();

// The operator channel (spec: docs/operator-channel.md). Counts and statistics to a
// private Telegram chat, and admin commands back. Every registration below is
// substitutable with a disabled implementation, because a hub with no bot token must
// behave exactly as it did before this existed.
builder.Services.Configure<OperatorChannelOptions>(
    builder.Configuration.GetSection(OperatorChannelOptions.Section));

builder.Services.AddSingleton<OperatorStateStore>();
builder.Services.AddSingleton<OperatorQueue>();
builder.Services.AddSingleton<UsageCounters>();
builder.Services.AddSingleton<FailureRates>();
builder.Services.AddSingleton<IHubAdministration, HubAdministration>();

// One HttpClient for both directions. Its timeout has to exceed the 30-second long
// poll, or every idle getUpdates would surface as a cancelled request.
//
// The loggers are removed, and that is not tidiness. The Bot API puts the token in the
// *path*, and the default HttpClient logging writes the request URI at Information
// level — so leaving it on publishes the credential into App Service's log stream on
// every poll, forever, where TelegramBotApi itself takes care to log only status codes.
// A leak nobody would find by reading this project's own code.
builder.Services.AddHttpClient<TelegramBotApi>().RemoveAllLoggers();

builder.Services.AddSingleton<IOperatorNotifier>(services =>
    services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.Configured
        ? services.GetRequiredService<OperatorQueue>()
        : new DisabledOperatorNotifier());

builder.Services.AddSingleton<IOperatorSender>(services =>
    services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.Configured
        ? services.GetRequiredService<TelegramBotApi>()
        : new DisabledOperatorSender());

builder.Services.AddSingleton<IOperatorUpdateSource>(services =>
    services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.CommandsEnabled
        ? services.GetRequiredService<TelegramBotApi>()
        : new DisabledOperatorSender());

// Counting is the one part that touches the relay hot path, so an unconfigured hub
// gets a recorder that does nothing at all rather than one that accumulates numbers
// nobody will ever read.
builder.Services.AddSingleton<IUsageRecorder>(services =>
    services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.Configured
        ? services.GetRequiredService<UsageCounters>()
        : new NullUsageRecorder());

builder.Services.AddHostedService<OperatorDispatcher>();
builder.Services.AddHostedService<OperatorStateFlusher>();

// Registered as itself and then handed to the host, rather than AddHostedService<T>,
// because /digest asks it for a report on demand and AddHostedService only ever
// exposes it as an IHostedService. Same shape as PushQueue/IPushNotifier above: one
// instance, two ways of reaching it.
builder.Services.AddSingleton<WeeklyDigestService>();
builder.Services.AddHostedService(services => services.GetRequiredService<WeeklyDigestService>());

builder.Services.AddHostedService<OperatorCommandService>();
builder.Services.AddHostedService<ClientSecretWatch>();

// Projects (issue #110). The hub's second piece of durable state, after the
// operator state above - see ProjectStore for why a file rather than a database.
// Session-to-project assignment itself lives in the registry's existing session
// labels alongside CustomName/Pinned, not here: this store only holds the project
// definitions themselves (name, description, urls, icon).
builder.Services.Configure<ProjectsOptions>(builder.Configuration.GetSection(ProjectsOptions.Section));
builder.Services.AddSingleton<ProjectStore>();

builder.Services
    .AddSignalR(RelayLiveness.Apply)
    // MessagePack rather than JSON because terminal output is binary. JSON would
    // base64 every frame, paying a third more bytes on the one path that is hot.
    .AddMessagePackProtocol();

var app = builder.Build();

// The app is served by the hub, from the hub's own origin. That is not a packaging
// convenience: same-origin means no CORS on the SignalR endpoint, one TLS
// certificate, one redirect URI registered in Entra, and no second thing to deploy
// and keep in step. The phone fetches the app and opens its socket back to the host
// it came from.
//
// Static files come before authentication because the app shell is public. It has to
// be: the sign-in screen is part of the bundle, so a pipeline that demanded a token
// to serve it could never hand anybody the page that obtains one.
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = WebManifestAware(),
    OnPrepareResponse = context => CacheFor(context),
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Deployed behind App Service, which terminates TLS and forwards over plain HTTP,
// so the app itself does no HTTPS redirection.
app.MapGet("/health", () => Results.Ok(new HealthResponse(
    Status: "ok",
    Version: ProductVersion.Current,
    UtcNow: DateTimeOffset.UtcNow)));

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

// Voice diagnostics contain configuration names and safe public choices, never the
// resource key or a usable token. Authentication keeps even deployment topology on
// the same surface as the rest of the signed-in app.
app.MapGet("/api/voice/health", [Authorize] (IOptions<AzureSpeechOptions> configured) =>
{
    AzureSpeechOptions speech = configured.Value;
    return Results.Ok(new VoiceHealthResponse(
        Status: speech.Configured ? "ready" : "not_configured",
        Provider: "Azure AI Speech",
        Region: speech.Configured ? speech.Region : null,
        RecognitionLanguage: speech.RecognitionLanguage,
        VoiceName: speech.VoiceName,
        MaxUtteranceSeconds: 30,
        MaxRecognizedTextCharacters: 4000,
        MaxSpokenTextCharacters: 2000));
});

app.MapPost("/api/voice/token", [Authorize] async (
    HttpContext context,
    ISpeechTokenBroker tokens,
    CancellationToken cancellationToken) =>
{
    if (UserKey.From(context.User) is null)
    {
        return Results.Unauthorized();
    }

    try
    {
        SpeechTokenGrant grant = await tokens.GetAsync(cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Ok(grant);
    }
    catch (InvalidOperationException error)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Voice service is not configured",
            detail: error.Message);
    }
    catch (SpeechProviderException error)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Voice provider is unavailable",
            detail: error.Message);
    }
}).RequireRateLimiting("voice-token");

// Project icons (issue #110). Deliberately plain, authenticated HTTP next to the
// SignalR hub rather than messages on it: an upload is a binary blob up to
// ProjectStore.MaxIconBytes, and pushing that through MessagePack would bloat the
// one payload every client refreshes on every list load. Ownership is enforced the
// same way every other per-user lookup in this hub is - by resolving the caller's
// own user key and never accepting one as a parameter - so a guessed project id
// from another account's icon can find nothing to read, replace, or clear.
//
// A successful upload or clear still needs to reach every other device this user
// has open, exactly like an edit made through UpdateProject - otherwise a phone
// showing the project list would carry a stale icon until its next reconnect. So
// these two mutating endpoints broadcast the same ProjectUpdatedNotification
// RelayHub.UpdateProject sends, via the same IHubContext/RelayRegistry pairing the
// hub itself would use if this were a hub method instead of a file upload.
app.MapPost("/projects/{projectId}/icon", [Authorize] async (
    string projectId,
    HttpRequest request,
    ProjectStore projects,
    RelayRegistry registry,
    IHubContext<RelayHub> hub,
    ClaimsPrincipal user) =>
{
    string? userKey = UserKey.From(user);
    if (userKey is null)
    {
        return Results.Unauthorized();
    }

    string? contentType = request.ContentType;
    if (string.IsNullOrWhiteSpace(contentType))
    {
        return Results.BadRequest(new { error = ErrorCodes.InvalidRequest });
    }

    // Read with an independent cap rather than trusting Content-Length, which a
    // client can misreport; this is what actually bounds memory use per upload.
    byte[] bytes;
    using (var buffer = new MemoryStream())
    {
        byte[] chunk = new byte[81_920];
        long total = 0;
        int read;

        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            total += read;

            if (total > ProjectStore.MaxIconBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        bytes = buffer.ToArray();
    }

    if (!projects.TrySetIcon(userKey, projectId, bytes, contentType, out ProjectInfo? project, out string? error))
    {
        return error == ErrorCodes.ProjectNotFound
            ? Results.NotFound()
            : Results.BadRequest(new { error });
    }

    await hub.Clients.Clients(registry.ClientsOf(userKey)).SendAsync(
        HubMethods.Client.ProjectUpdated,
        new ProjectUpdatedNotification { Project = project! });

    return Results.Ok(new ProjectIconResponse(project!.IconVersion));
});

app.MapGet("/projects/{projectId}/icon", [Authorize] (
    string projectId,
    ProjectStore projects,
    ClaimsPrincipal user) =>
{
    string? userKey = UserKey.From(user);
    if (userKey is null)
    {
        return Results.Unauthorized();
    }

    return projects.TryReadIcon(userKey, projectId, out byte[]? bytes, out string? contentType)
        ? Results.File(bytes!, contentType!)
        : Results.NotFound();
});

app.MapDelete("/projects/{projectId}/icon", [Authorize] async (
    string projectId,
    ProjectStore projects,
    RelayRegistry registry,
    IHubContext<RelayHub> hub,
    ClaimsPrincipal user) =>
{
    string? userKey = UserKey.From(user);
    if (userKey is null)
    {
        return Results.Unauthorized();
    }

    if (!projects.TryClearIcon(userKey, projectId, out ProjectInfo? project, out string? error))
    {
        return Results.NotFound();
    }

    await hub.Clients.Clients(registry.ClientsOf(userKey)).SendAsync(
        HubMethods.Client.ProjectUpdated,
        new ProjectUpdatedNotification { Project = project! });

    return Results.Ok(new ProjectIconResponse(project!.IconVersion));
});

// Must match EntraAuthenticationExtensions.HubPathPrefix, the only path where a token
// is accepted from the query string.
app.MapHub<RelayHub>("/hub");

// The app itself, when a build has been staged into wwwroot. A hub without one is
// still a working relay — that is how the tests and `dotnet run` use it — so a
// missing bundle is a different deployment, not a broken one.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    // Every path the app routes to client-side has to return the shell, because a
    // phone that reloads on a deep link asks the server for a path only the browser
    // knows about. The `:nonfile` constraint keeps a genuinely missing asset a 404
    // rather than an HTML page delivered under a .js content type.
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        ContentTypeProvider = WebManifestAware(),
        OnPrepareResponse = CacheFor,
    });
}
else
{
    app.MapGet("/", () => Results.Text("1RemoteCLI hub", "text/plain"));
}

if (!app.Services.GetRequiredService<IOptions<VapidOptions>>().Value.Configured)
{
    app.Logger.LogWarning(
        "Push is disabled: no VAPID keypair configured under '{Section}'. " +
        "Sessions still work; phones will not be told when one is waiting.",
        VapidOptions.Section);
}

if (!app.Services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.Configured)
{
    app.Logger.LogInformation(
        "The operator channel is off: no bot token and chat id under '{Section}'. " +
        "Nothing is counted and nothing is reported.",
        OperatorChannelOptions.Section);
}

if (!app.Services.GetRequiredService<IOptions<AzureSpeechOptions>>().Value.Configured)
{
    app.Logger.LogInformation(
        "Voice mode is off: Azure Speech is not configured under '{Section}'.",
        AzureSpeechOptions.Section);
}

// Replays the allowlist amendments made by /allow and /deny before the first request
// is served, so a restart does not silently undo an admission the operator made from
// their phone. Also announces the restart itself.
OperatorStartup.Begin(app.Services, app.Logger);

app.Run();

/// <summary>
/// Content types, plus the one the framework does not know.
/// <para>
/// A web app manifest served as <c>application/octet-stream</c> is ignored by every
/// browser, and the symptom is not an error: the app simply cannot be installed to a
/// home screen. On iOS that also means it can never receive a notification, so this
/// one missing MIME entry would quietly remove the feature this product exists for.
/// </para>
/// </summary>
static FileExtensionContentTypeProvider WebManifestAware()
{
    var provider = new FileExtensionContentTypeProvider();
    provider.Mappings[".webmanifest"] = "application/manifest+json";

    return provider;
}

/// <summary>
/// How long the phone may keep each file.
/// <para>
/// Two rules, and the split is what makes an update actually arrive. Vite names every
/// bundled asset after a hash of its contents, so those files can never change meaning
/// and are cached for a year. Everything else — the shell, the service worker, the
/// manifest — keeps its name across releases, so it must be revalidated every time.
/// Caching <c>sw.js</c> is the classic way to ship a service worker that can never be
/// replaced: the browser keeps serving the old one, which keeps serving the old app,
/// and no amount of redeploying fixes it.
/// </para>
/// </summary>
static void CacheFor(StaticFileResponseContext context)
{
    bool fingerprinted = context.Context.Request.Path
        .StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase);

    context.Context.Response.Headers.CacheControl = fingerprinted
        ? "public, max-age=31536000, immutable"
        : "no-cache";
}

internal sealed record VapidKeyResponse(string Key);

internal sealed record HealthResponse(string Status, string Version, DateTimeOffset UtcNow);

internal sealed record WhoAmIResponse(string? UserKey, string? Username);

internal sealed record ProjectIconResponse(int IconVersion);

internal sealed record VoiceHealthResponse(
    string Status,
    string Provider,
    string? Region,
    string RecognitionLanguage,
    string VoiceName,
    int MaxUtteranceSeconds,
    int MaxRecognizedTextCharacters,
    int MaxSpokenTextCharacters);

/// <summary>Exposed so tests can host this exact application.</summary>
public partial class Program;
