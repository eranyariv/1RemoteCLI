using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Speech;

public sealed record SpeechTokenGrant(
    string Token,
    string Region,
    string RecognitionLanguage,
    string VoiceName,
    DateTimeOffset ExpiresAt);

public interface ISpeechTokenBroker
{
    Task<SpeechTokenGrant> GetAsync(CancellationToken cancellationToken);
}

public sealed class SpeechProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Exchanges a server-held Speech resource key for a short-lived client token.
/// </summary>
public sealed class AzureSpeechTokenBroker(
    IHttpClientFactory clients,
    IOptions<AzureSpeechOptions> configured,
    TimeProvider timeProvider) : ISpeechTokenBroker
{
    public const string HttpClientName = "AzureSpeech";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);
    private const int MaxTokenChars = 16 * 1024;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private SpeechTokenGrant? _cached;

    public async Task<SpeechTokenGrant> GetAsync(CancellationToken cancellationToken)
    {
        AzureSpeechOptions options = configured.Value;
        if (!options.Configured)
        {
            throw new InvalidOperationException(
                $"Azure Speech is not configured under '{AzureSpeechOptions.Section}'.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        SpeechTokenGrant? cached = _cached;
        if (cached is not null && cached.ExpiresAt - now > RefreshMargin)
        {
            return cached;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            cached = _cached;
            if (cached is not null && cached.ExpiresAt - now > RefreshMargin)
            {
                return cached;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://{options.Region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
            request.Headers.Add("Ocp-Apim-Subscription-Key", options.SubscriptionKey);
            request.Content = new ByteArrayContent([]);

            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception error) when (
                error is HttpRequestException ||
                error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new SpeechProviderException("Azure Speech token exchange could not be reached.", error);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new SpeechProviderException(
                        $"Azure Speech token exchange returned HTTP {(int)response.StatusCode}.");
                }

                string token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (token.Length is 0 or > MaxTokenChars)
                {
                    throw new SpeechProviderException("Azure Speech returned an invalid token.");
                }

                _cached = new SpeechTokenGrant(
                    token,
                    options.Region,
                    options.RecognitionLanguage,
                    options.VoiceName,
                    now + TokenLifetime);
                return _cached;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }
}
