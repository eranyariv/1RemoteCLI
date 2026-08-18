using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// How the operator channel is assembled, tested through the real application rather
/// than a hand-built container — because every defect this file guards against is a
/// wiring defect, invisible in the components themselves.
/// </summary>
public sealed class OperatorWiringTests
{
    /// <summary>
    /// <b>The bot token must never reach a log.</b>
    /// <para>
    /// The Bot API puts the token in the URL path, and <c>AddHttpClient</c> installs a
    /// handler that logs the request URI at Information level. So a hub that is careful
    /// everywhere in its own code still publishes its credential into the App Service log
    /// stream on every poll — twice a minute, forever — and nothing in the project's own
    /// source would show you that.
    /// </para>
    /// <para>
    /// It was found by running the thing and reading the console. This test is here so it
    /// cannot come back the next time somebody adds a handler or re-registers the client.
    /// </para>
    /// </summary>
    [Fact]
    public void TheBotTokenIsNeverWrittenToALogByTheHttpStack()
    {
        using var factory = new WebApplicationFactory<Program>();

        HttpMessageHandler handler = factory.Services
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(TelegramBotApi));

        List<string> chain = [];

        for (HttpMessageHandler? link = handler; link is DelegatingHandler delegating; link = delegating.InnerHandler)
        {
            chain.Add(link.GetType().Name);
        }

        // Asserted against the handler chain rather than a configuration flag, because
        // the chain is what actually runs. Both of the framework's logging handlers write
        // the request URI.
        Assert.DoesNotContain("LoggingHttpMessageHandler", chain, StringComparer.Ordinal);
        Assert.DoesNotContain("LoggingScopeHttpMessageHandler", chain, StringComparer.Ordinal);
    }

    /// <summary>
    /// An unconfigured hub must behave exactly as it did before this feature existed:
    /// nothing counted, nothing sent, no background work. Anyone should be able to work
    /// on the relay without provisioning a bot.
    /// </summary>
    [Fact]
    public void WithNoTokenTheChannelIsInertRatherThanBroken()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.False(factory.Services.GetRequiredService<IOptions<OperatorChannelOptions>>().Value.Configured);
        Assert.IsType<DisabledOperatorNotifier>(factory.Services.GetRequiredService<IOperatorNotifier>());
        Assert.IsType<DisabledOperatorSender>(factory.Services.GetRequiredService<IOperatorSender>());

        // The relay hot path in particular: an unconfigured hub does not accumulate
        // numbers nobody will ever read.
        Assert.IsType<NullUsageRecorder>(factory.Services.GetRequiredService<IUsageRecorder>());
    }

    /// <summary>
    /// Inbound commands are off unless asked for, even when the channel is otherwise
    /// configured. Reporting only leaves the token unable to do anything but talk, which
    /// is the right default for a credential that can otherwise change the allowlist.
    /// </summary>
    [Fact]
    public void ReportingDoesNotImplyAcceptingCommands()
    {
        var configured = new OperatorChannelOptions { BotToken = "token", ChatId = "chat" };

        Assert.True(configured.Configured);
        Assert.False(configured.CommandsEnabled);

        configured.Commands = true;
        Assert.True(configured.CommandsEnabled);
    }

    /// <summary>Commands cannot be switched on without a channel to receive them over.</summary>
    [Fact]
    public void CommandsCannotBeEnabledWithoutAChannel()
    {
        Assert.False(new OperatorChannelOptions { Commands = true }.CommandsEnabled);
        Assert.False(new OperatorChannelOptions { Commands = true, BotToken = "token" }.CommandsEnabled);
    }

    /// <summary>
    /// The digest service is reachable as itself and not only as a hosted service, because
    /// <c>/digest</c> asks it for a report on demand. Registering it with
    /// <c>AddHostedService&lt;T&gt;</c> alone compiles and then fails at startup.
    /// </summary>
    [Fact]
    public void TheDigestCanBeAskedForOnDemand()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.NotNull(factory.Services.GetService<WeeklyDigestService>());
    }
}
