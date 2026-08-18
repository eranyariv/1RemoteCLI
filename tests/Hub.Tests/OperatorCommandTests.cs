using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The inbound half, where the bot token stops being a write-only credential.
/// <para>
/// Parsing is pure and tested exhaustively because the alternative is discovering the
/// awkward forms by typing at a production hub that can change the allowlist. Every one
/// of these came from the Bot API's actual behaviour rather than from imagination: the
/// <c>@botname</c> suffix, the bare slash, the argument with spaces in it.
/// </para>
/// </summary>
public class OperatorCommandTests
{
    [Theory]
    [InlineData("/status", "status", "")]
    [InlineData("/STATUS", "status", "")]
    [InlineData("  /status  ", "status", "")]
    [InlineData("/allow someone@example.com", "allow", "someone@example.com")]
    [InlineData("/allow   someone@example.com  ", "allow", "someone@example.com")]
    public void OrdinaryCommandsParse(string text, string name, string argument)
    {
        OperatorCommand command = OperatorCommand.Parse(text);

        Assert.Equal(name, command.Name);
        Assert.Equal(argument, command.Argument);
        Assert.True(command.IsCommand);
    }

    /// <summary>
    /// Telegram appends the bot's username in groups, and some clients do it from the
    /// command menu in a private chat too. Left in place, every menu-issued command would
    /// come back "unknown command" — which looks exactly like a broken bot.
    /// </summary>
    [Theory]
    [InlineData("/status@OneRemoteCLIAdmin_bot", "status", "")]
    [InlineData("/allow@OneRemoteCLIAdmin_bot someone@example.com", "allow", "someone@example.com")]
    public void TheBotSuffixIsStripped(string text, string name, string argument)
    {
        OperatorCommand command = OperatorCommand.Parse(text);

        Assert.Equal(name, command.Name);
        Assert.Equal(argument, command.Argument);
    }

    /// <summary>
    /// A broadcast is a sentence, so everything after the first space is the argument —
    /// not just the first word.
    /// </summary>
    [Fact]
    public void AnArgumentKeepsItsSpaces()
    {
        Assert.Equal(
            "the hub is going down for ten minutes",
            OperatorCommand.Parse("/broadcast the hub is going down for ten minutes").Argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("hello")]
    [InlineData("what is the status")]
    [InlineData("mailto:someone@example.com")]
    public void AnythingThatIsNotACommandIsNotOne(string text)
    {
        Assert.False(OperatorCommand.Parse(text).IsCommand);
    }

    [Fact]
    public void NoTextAtAllIsNotACommand()
    {
        Assert.False(OperatorCommand.Parse(null).IsCommand);
    }

    [Theory]
    [InlineData("/status", "status")]
    [InlineData("/health", "health")]
    [InlineData("/version", "version")]
    [InlineData("/kick a@b.c", "kick")]
    [InlineData("/broadcast hello", "broadcast")]
    public void EachCommandReachesItsOwnOperation(string text, string expected)
    {
        var administration = new RecordingAdministration();

        OperatorCommands.Execute(OperatorCommand.Parse(text), administration, () => { });

        Assert.Equal(expected, administration.Called);
    }

    /// <summary>
    /// <c>/allow</c> and <c>/deny</c> are the two that change who can sign in, so the
    /// argument reaching them exactly as typed is the whole correctness of the feature.
    /// </summary>
    [Theory]
    [InlineData("/allow tenant-guid:object-guid", "allow", "tenant-guid:object-guid")]
    [InlineData("/deny someone@example.com", "deny", "someone@example.com")]
    public void TheAccountReachesTheAllowlistUnaltered(string text, string operation, string account)
    {
        var administration = new RecordingAdministration();

        OperatorCommands.Execute(OperatorCommand.Parse(text), administration, () => { });

        Assert.Equal(operation, administration.Called);
        Assert.Equal(account, administration.Argument);
    }

    /// <summary>
    /// A digest is sent rather than returned, because sending it also closes the week —
    /// the reply is only the acknowledgement.
    /// </summary>
    [Fact]
    public void AskingForADigestSendsOneAndAcknowledges()
    {
        var sent = false;

        OperatorMessage reply = OperatorCommands.Execute(
            OperatorCommand.Parse("/digest"),
            new RecordingAdministration(),
            () => sent = true);

        Assert.True(sent);
        Assert.IsType<OperatorMessage.DigestRequested>(reply);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/start")]
    public void HelpAndStartBothExplainTheChannel(string text)
    {
        Assert.IsType<OperatorMessage.Help>(
            OperatorCommands.Execute(OperatorCommand.Parse(text), new RecordingAdministration(), () => { }));
    }

    /// <summary>
    /// An unrecognised command is answered. A bot that silently does nothing is
    /// indistinguishable from a bot that is down, which is the worst state for the one
    /// channel the operator uses to check whether things are working.
    /// </summary>
    [Fact]
    public void AnUnknownCommandIsAnswered()
    {
        OperatorMessage reply = OperatorCommands.Execute(
            OperatorCommand.Parse("/frobnicate"),
            new RecordingAdministration(),
            () => { });

        Assert.Equal(CommandFault.Unknown, Assert.IsType<OperatorMessage.CommandRejected>(reply).Fault);
    }

    private sealed class RecordingAdministration : IHubAdministration
    {
        public string? Called { get; private set; }

        public string? Argument { get; private set; }

        public OperatorMessage Status() => Record("status", null);

        public OperatorMessage Health() => Record("health", null);

        public OperatorMessage Version() => Record("version", null);

        public OperatorMessage Allow(string account) => Record("allow", account);

        public OperatorMessage Deny(string account) => Record("deny", account);

        public OperatorMessage Kick(string account) => Record("kick", account);

        public OperatorMessage Broadcast(string text) => Record("broadcast", text);

        private OperatorMessage Record(string operation, string? argument)
        {
            Called = operation;
            Argument = argument;

            return new OperatorMessage.VersionReport("1.0.0");
        }
    }
}
