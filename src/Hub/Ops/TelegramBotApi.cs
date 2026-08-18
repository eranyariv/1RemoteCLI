using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Ops;

/// <summary>Delivers one already-rendered message to the operator's chat.</summary>
public interface IOperatorSender
{
    Task SendAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>One inbound message, reduced to the three fields the hub acts on.</summary>
/// <param name="UpdateId">The Bot API's cursor. Acknowledged by asking for the next one.</param>
/// <param name="ChatId">Who sent it. Checked against configuration on every update, never trusted.</param>
/// <param name="Text">What they typed.</param>
public readonly record struct OperatorUpdate(long UpdateId, string ChatId, string Text);

/// <summary>Where inbound commands come from.</summary>
public interface IOperatorUpdateSource
{
    /// <summary>
    /// Waits for messages newer than <paramref name="offset"/>, returning an empty list
    /// if none arrive before the long poll times out.
    /// </summary>
    Task<IReadOnlyList<OperatorUpdate>> PollAsync(long offset, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Telegram Bot API, both directions.
/// <para>
/// <b>Inbound is long polling, not a webhook.</b> A webhook would mean a public,
/// unauthenticated endpoint on a hub that currently has none — every route here either
/// requires a token or serves the app shell — and it would have to be reachable from
/// the internet, which rules out ever running this on a developer machine. Long polling
/// is a plain outbound HTTPS call that works identically everywhere and leaves the hub's
/// attack surface exactly as it was.
/// </para>
/// <para>
/// The token is in the URL because that is the only place the Bot API accepts it. It
/// therefore must not be logged: every message here reports a status code and nothing
/// else, and the base address is built once rather than interpolated at each call site.
/// </para>
/// </summary>
public sealed class TelegramBotApi : IOperatorSender, IOperatorUpdateSource
{
    /// <summary>
    /// How long a poll waits at Telegram's end before returning empty.
    /// <para>
    /// Long enough that an idle hub makes two requests a minute rather than sixty, and
    /// comfortably inside the client's own timeout below — a client that gave up before
    /// the server answered would look exactly like a network fault, forever.
    /// </para>
    /// </summary>
    private const int PollSeconds = 30;

    /// <summary>Telegram rejects anything longer. Truncated rather than lost.</summary>
    private const int MaxMessage = 4096;

    /// <summary>
    /// How many times one message is offered to Telegram before it is given up on.
    /// <para>
    /// Two, and only for a rate limit. Honouring <c>Retry-After</c> and then not resending
    /// is not a policy, it is a dropped message with extra steps — and the messages this
    /// channel carries are alerts, so the one lost to a burst is disproportionately likely
    /// to be the one that mattered. Bounded at two because the dispatcher is a single
    /// serial queue: retrying indefinitely would let one rejected message hold up every
    /// report behind it, which is a worse failure than losing it.
    /// </para>
    /// </summary>
    private const int SendAttempts = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly OperatorChannelOptions _options;
    private readonly ILogger<TelegramBotApi> _logger;

    public TelegramBotApi(
        HttpClient client,
        IOptions<OperatorChannelOptions> options,
        ILogger<TelegramBotApi> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;

        // Must outlast the long poll, or every idle minute looks like a timeout.
        _client.Timeout = TimeSpan.FromSeconds(PollSeconds + 30);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_options.Configured || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var request = new SendMessageRequest(
            _options.ChatId,
            text.Length > MaxMessage ? text[..MaxMessage] : text,
            DisableWebPagePreview: true);

        for (int attempt = 1; ; attempt++)
        {
            using HttpResponseMessage response = await _client
                .PostAsJsonAsync(Method("sendMessage"), request, Json, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // Honoured rather than retried blindly: the Bot API says how long to wait, and
            // ignoring it is how a rate limit becomes a ban.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < SendAttempts)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                _logger.LogWarning("Telegram is rate limiting; waiting {Seconds}s.", (int)wait.TotalSeconds);

                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Status only. The response body of a failed Bot API call quotes the request,
            // and the request contains the message.
            _logger.LogWarning("Telegram refused a message ({Status}).", (int)response.StatusCode);
            return;
        }
    }

    public async Task<IReadOnlyList<OperatorUpdate>> PollAsync(
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CommandsEnabled)
        {
            return [];
        }

        // allowed_updates keeps everything but plain messages out. There is nothing the
        // hub does with an edited message or a channel post, and asking for them only
        // creates shapes to defend against.
        string url = $"{Method("getUpdates")}?timeout={PollSeconds}&allowed_updates=%5B%22message%22%5D" +
                     (offset > 0 ? $"&offset={offset}" : string.Empty);

        using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telegram refused a poll ({Status}).", (int)response.StatusCode);
            return [];
        }

        UpdatesResponse? updates = await response.Content
            .ReadFromJsonAsync<UpdatesResponse>(Json, cancellationToken)
            .ConfigureAwait(false);

        if (updates?.Ok != true || updates.Result is null)
        {
            return [];
        }

        return [.. updates.Result
            .Where(update => update.Message?.Chat is not null && update.Message.Text is not null)
            .Select(update => new OperatorUpdate(
                update.UpdateId,
                update.Message!.Chat!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                update.Message.Text!))];
    }

    private string Method(string name) =>
        $"https://api.telegram.org/bot{_options.BotToken}/{name}";

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);

    private sealed record UpdatesResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] IReadOnlyList<Update>? Result);

    private sealed record Update(
        [property: JsonPropertyName("update_id")] long UpdateId,
        [property: JsonPropertyName("message")] Message? Message);

    private sealed record Message(
        [property: JsonPropertyName("chat")] Chat? Chat,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record Chat([property: JsonPropertyName("id")] long Id);
}

/// <summary>
/// No bot is configured. Sending is a no-op and polling never returns anything.
/// <para>
/// The unconfigured hub is a first-class state, not a degraded one: it is what every
/// test and every <c>dotnet run</c> uses.
/// </para>
/// </summary>
public sealed class DisabledOperatorSender : IOperatorSender, IOperatorUpdateSource
{
    public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<OperatorUpdate>> PollAsync(long offset, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OperatorUpdate>>([]);
}
