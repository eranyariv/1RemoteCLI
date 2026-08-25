using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OneRemoteCli.Hub.Speech;

namespace OneRemoteCli.Hub.Tests;

public sealed class VoiceEndpointTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Object = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Fact]
    public async Task VoiceDiagnosticsAndTokensRequireTheExistingUserIdentity()
    {
        using WebApplicationFactory<Program> factory = Factory(new StubTokenBroker());
        using HttpClient client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/voice/health")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/voice/token", content: null)).StatusCode);
    }

    [Fact]
    public async Task ReturnsSafeDiagnosticsAndANonCacheableShortLivedToken()
    {
        using WebApplicationFactory<Program> factory = Factory(new StubTokenBroker());
        using HttpClient client = factory.CreateClient();
        SignIn(client);

        HttpResponseMessage health = await client.GetAsync("/api/voice/health");
        string healthJson = await health.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"status\":\"ready\"", healthJson, StringComparison.Ordinal);
        Assert.Contains("\"maxUtteranceSeconds\":30", healthJson, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", healthJson, StringComparison.Ordinal);

        HttpResponseMessage token = await client.PostAsync("/api/voice/token", content: null);
        JsonElement grant = (await token.Content.ReadFromJsonAsync<JsonElement>());

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.True(token.Headers.CacheControl?.NoStore);
        Assert.Equal("short-lived-token", grant.GetProperty("token").GetString());
        Assert.Equal("eastus", grant.GetProperty("region").GetString());
        Assert.DoesNotContain("server-secret", await token.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailuresAreExplicitAndDoNotLookLikeAUsableGrant()
    {
        using WebApplicationFactory<Program> factory = Factory(
            new StubTokenBroker(new SpeechProviderException("Azure Speech returned HTTP 429.")));
        using HttpClient client = factory.CreateClient();
        SignIn(client);

        HttpResponseMessage response = await client.PostAsync("/api/voice/token", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("HTTP 429", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenGrantsAreRateLimitedPerSignedInUser()
    {
        using WebApplicationFactory<Program> factory = Factory(new StubTokenBroker());
        using HttpClient client = factory.CreateClient();
        SignIn(client);

        for (var index = 0; index < 12; index++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsync("/api/voice/token", content: null)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsync("/api/voice/token", content: null)).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(ISpeechTokenBroker tokens) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AzureSpeech:Region", "eastus");
            builder.UseSetting("AzureSpeech:SubscriptionKey", "server-secret");
            builder.UseSetting("AzureSpeech:RecognitionLanguage", "en-US");
            builder.UseSetting("AzureSpeech:VoiceName", "en-US-AvaMultilingualNeural");
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityHandler>(
                        HeaderIdentityHandler.SchemeName,
                        _ => { });
                services.RemoveAll<ISpeechTokenBroker>();
                services.AddSingleton(tokens);
            });
        });

    private static void SignIn(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(HeaderIdentityHandler.TenantHeader, Tenant);
        client.DefaultRequestHeaders.Add(HeaderIdentityHandler.ObjectHeader, Object);
    }

    private sealed class StubTokenBroker(Exception? failure = null) : ISpeechTokenBroker
    {
        public Task<SpeechTokenGrant> GetAsync(CancellationToken cancellationToken) =>
            failure is null
                ? Task.FromResult(new SpeechTokenGrant(
                    "short-lived-token",
                    "eastus",
                    "en-US",
                    "en-US-AvaMultilingualNeural",
                    DateTimeOffset.UtcNow.AddMinutes(9)))
                : Task.FromException<SpeechTokenGrant>(failure);
    }
}
