using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The hub serving the phone app.
/// <para>
/// The app is delivered by the hub, from the hub's own origin, which is what makes
/// the SignalR endpoint same-origin and removes CORS from the design entirely. That
/// puts a static file pipeline in front of the relay, and a static file pipeline has
/// three ways to be quietly wrong: it can swallow an API route, it can hand back the
/// wrong content type, or it can cache something that must never be cached. None of
/// them produce an error anywhere — they produce a phone that cannot install the app,
/// or cannot ever be updated.
/// </para>
/// </summary>
public sealed class StaticHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"1remote-www-{Guid.NewGuid():n}");

    public StaticHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));

        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><title>1RemoteCLI</title>");
        File.WriteAllText(Path.Combine(_root, "sw.js"), "self.addEventListener('install', () => {})");
        File.WriteAllText(Path.Combine(_root, "manifest.webmanifest"), "{\"name\":\"1RemoteCLI\"}");
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc123.js"), "export default 1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HttpClient Served() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseWebRoot(_root))
            .CreateClient();

    [Fact]
    public async Task ServesTheAppAtTheRoot()
    {
        HttpResponseMessage response = await Served().GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("1RemoteCLI", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A phone that reloads while watching a session asks the server for a path only
    /// the browser knows how to route. Returning 404 there would mean the app worked
    /// until the moment somebody refreshed it.
    /// </summary>
    [Fact]
    public async Task ServesTheAppForAPathOnlyTheBrowserKnows()
    {
        HttpResponseMessage response = await Served()
            .GetAsync(new Uri("/machines/desk/sessions/s-1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A missing asset must stay missing. Falling back to the shell would answer a
    /// request for a script with a page of HTML, and the browser would report a syntax
    /// error in a file that does not exist.
    /// </summary>
    [Fact]
    public async Task DoesNotAnswerAMissingAssetWithTheApp()
    {
        HttpResponseMessage response = await Served()
            .GetAsync(new Uri("/assets/index-doesnotexist.js", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ServesTheManifestAsAManifest()
    {
        HttpResponseMessage response = await Served()
            .GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Hashed assets can never change meaning, so they are cached for a year. This is
    /// the only reason a phone on a cellular link does not refetch xterm on every
    /// launch.
    /// </summary>
    [Fact]
    public async Task CachesFingerprintedAssetsForever()
    {
        HttpResponseMessage response = await Served()
            .GetAsync(new Uri("/assets/index-abc123.js", UriKind.Relative));

        CacheControlHeaderValue? cache = response.Headers.CacheControl;

        Assert.NotNull(cache);
        Assert.True(cache.Public);
        Assert.Equal(TimeSpan.FromDays(365), cache.MaxAge);
    }

    /// <summary>
    /// The counterpart, and the more important half. A cached service worker is a
    /// service worker that can never be replaced: the browser keeps serving the old
    /// one, which keeps serving the old app, and redeploying does not help.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/sw.js")]
    [InlineData("/manifest.webmanifest")]
    public async Task NeverCachesTheFilesThatKeepTheirNames(string path)
    {
        HttpResponseMessage response = await Served().GetAsync(new Uri(path, UriKind.Relative));

        Assert.True(
            response.Headers.CacheControl?.NoCache,
            $"{path} was served with '{response.Headers.CacheControl}', which would pin an old build.");
    }

    /// <summary>
    /// The routes that are not the app. A catch-all fallback that shadowed one of
    /// these would break the product while the app itself kept loading perfectly.
    /// </summary>
    [Fact]
    public async Task LeavesTheApiRoutesAlone()
    {
        HttpClient client = Served();

        HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal("application/json", health.Content.Headers.ContentType?.MediaType);

        // Unconfigured push answers 404 rather than the app shell.
        HttpResponseMessage vapid = await client.GetAsync(new Uri("/push/vapid", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, vapid.StatusCode);
        Assert.NotEqual("text/html", vapid.Content.Headers.ContentType?.MediaType);

        // The relay endpoint refuses an anonymous caller. What matters is that it is
        // the hub answering at all, rather than the fallback handing back a page.
        HttpResponseMessage negotiate = await client.PostAsync(
            new Uri("/hub/negotiate?negotiateVersion=1", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    /// <summary>
    /// A hub with no app staged is a working relay, not a broken deployment. That is
    /// how every test in this repository and every `dotnet run` uses it.
    /// </summary>
    [Fact]
    public async Task RemainsAWorkingRelayWithNoAppStaged()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        HttpClient client = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseWebRoot(empty))
            .CreateClient();

        HttpResponseMessage root = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/plain", root.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
