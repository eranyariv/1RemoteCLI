using System.Net;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Speech;

namespace OneRemoteCli.Hub.Tests;

public sealed class AzureSpeechTokenBrokerTests
{
    [Fact]
    public async Task ExchangesTheServerKeyOnceAndCachesTheShortLivedToken()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("speech-token"),
            });
        var broker = Broker(handler);

        SpeechTokenGrant first = await broker.GetAsync(CancellationToken.None);
        SpeechTokenGrant second = await broker.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(
            "https://eastus.api.cognitive.microsoft.com/sts/v1.0/issueToken",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(
            "server-secret",
            Assert.Single(handler.LastRequest.Headers.GetValues("Ocp-Apim-Subscription-Key")));
        Assert.Equal("speech-token", first.Token);
        Assert.Equal("eastus", first.Region);
        Assert.DoesNotContain("server-secret", first.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingOrUnsafeConfigurationBeforeCallingAzure()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = Options.Create(new AzureSpeechOptions
        {
            Region = "https://attacker.example",
            SubscriptionKey = "server-secret",
        });
        var broker = new AzureSpeechTokenBroker(
            new SingleClientFactory(new HttpClient(handler)),
            options,
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.GetAsync(CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task SurfacesProviderFailureWithoutReturningAnEmptyToken()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        SpeechProviderException error = await Assert.ThrowsAsync<SpeechProviderException>(
            () => Broker(handler).GetAsync(CancellationToken.None));

        Assert.Contains("HTTP 429", error.Message, StringComparison.Ordinal);
    }

    private static AzureSpeechTokenBroker Broker(HttpMessageHandler handler) =>
        new(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new AzureSpeechOptions
            {
                Region = "eastus",
                SubscriptionKey = "server-secret",
                RecognitionLanguage = "en-US",
                VoiceName = "en-US-AvaMultilingualNeural",
            }),
            TimeProvider.System);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastRequest = request;
            return Task.FromResult(reply(request));
        }
    }
}
