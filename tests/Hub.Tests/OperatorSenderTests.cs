using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// What happens to a message between the queue and Telegram.
/// <para>
/// The failure these guard against is the quiet one: a report that the hub believes it
/// sent and the operator never sees. Nothing downstream would notice, because a channel
/// with nothing to say and a channel that is dropping everything look identical.
/// </para>
/// </summary>
public sealed class OperatorSenderTests
{
    /// <summary>
    /// <b>A rate-limited message is resent, not quietly discarded.</b>
    /// <para>
    /// The Bot API allows roughly one message a second to a chat. A burst — a restart
    /// raising several alerts at once, or an operator running a handful of commands —
    /// answers 429 for the ones over the line. Waiting for <c>Retry-After</c> and then
    /// returning would honour the limit and still lose the message, which is the exact
    /// outcome this whole channel exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AMessageTelegramRateLimitsIsSentAgainAfterTheWait()
    {
        var handler = new ScriptedHandler(
            Rate(TimeSpan.FromMilliseconds(1)),
            new HttpResponseMessage(HttpStatusCode.OK));

        TelegramBotApi api = Api(handler);

        await api.SendAsync("Push has started failing.");

        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// The retry is bounded, because the dispatcher is one serial queue: a message that
    /// kept being retried would hold up every report behind it, which is worse than
    /// losing it.
    /// </summary>
    [Fact]
    public async Task ItGivesUpRatherThanHoldingTheQueueForever()
    {
        var handler = new ScriptedHandler(
            Rate(TimeSpan.FromMilliseconds(1)),
            Rate(TimeSpan.FromMilliseconds(1)),
            Rate(TimeSpan.FromMilliseconds(1)));

        TelegramBotApi api = Api(handler);

        await api.SendAsync("Push has started failing.");

        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// Any other refusal is final. A 400 is a malformed request, and sending it a second
    /// time makes it no less malformed.
    /// </summary>
    [Fact]
    public async Task ARefusalThatIsNotARateLimitIsNotRetried()
    {
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));

        TelegramBotApi api = Api(handler);

        await api.SendAsync("Push has started failing.");

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>An unconfigured hub does not call Telegram at all.</summary>
    [Fact]
    public async Task NothingIsSentWhenThereIsNoBot()
    {
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var api = new TelegramBotApi(
            new HttpClient(handler),
            Options.Create(new OperatorChannelOptions()),
            NullLogger<TelegramBotApi>.Instance);

        await api.SendAsync("Push has started failing.");

        Assert.Equal(0, handler.Calls);
    }

    private static TelegramBotApi Api(ScriptedHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new OperatorChannelOptions
            {
                BotToken = "0000000:not-a-real-token",
                ChatId = "1",
            }),
            NullLogger<TelegramBotApi>.Instance);

    private static HttpResponseMessage Rate(TimeSpan after)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(after);
        return response;
    }

    /// <summary>Answers with the prepared responses in order, then repeats the last.</summary>
    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;

            return Task.FromResult(response);
        }
    }
}
