using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Ops;

/// <summary>One parsed command. Never more than a name and the rest of the line.</summary>
/// <param name="Name">Lower-cased, without the slash. Empty when the text was not a command.</param>
/// <param name="Argument">Everything after the first space, trimmed. Empty when there was none.</param>
public readonly record struct OperatorCommand(string Name, string Argument)
{
    public bool IsCommand => Name.Length > 0;

    /// <summary>
    /// Splits a message into a command and its argument.
    /// <para>
    /// A pure function, so every awkward form — a bot suffix, an argument containing
    /// spaces, a bare slash, an empty message — is a unit test rather than something
    /// discovered by typing at a production hub that can change the allowlist.
    /// </para>
    /// </summary>
    public static OperatorCommand Parse(string? text)
    {
        string line = (text ?? string.Empty).Trim();

        if (line.Length < 2 || line[0] != '/')
        {
            return new OperatorCommand(string.Empty, string.Empty);
        }

        int space = line.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? line[1..] : line[1..space];
        string argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();

        // Telegram appends @botname when a command is sent in a group and, on some
        // clients, when it is picked from the command menu. Left in place it would turn
        // every menu-issued command into "unknown command".
        int at = name.IndexOf('@', StringComparison.Ordinal);

        if (at >= 0)
        {
            name = name[..at];
        }

        return new OperatorCommand(name.ToLowerInvariant(), argument);
    }
}

/// <summary>
/// Turns a command into one message from the closed vocabulary.
/// <para>
/// A pure dispatch over a fixed set of names, kept out of the polling loop so the whole
/// command surface can be tested without a network. Anything unrecognised is answered
/// with the help text rather than ignored — a bot that silently does nothing is
/// indistinguishable from a bot that is down.
/// </para>
/// </summary>
public static class OperatorCommands
{
    public static OperatorMessage Execute(
        OperatorCommand command,
        IHubAdministration administration,
        Action sendDigest)
    {
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(sendDigest);

        switch (command.Name)
        {
            case "status":
                return administration.Status();

            case "health":
                return administration.Health();

            case "version":
                return administration.Version();

            case "allow":
                return administration.Allow(command.Argument);

            case "deny":
                return administration.Deny(command.Argument);

            case "kick":
                return administration.Kick(command.Argument);

            case "broadcast":
                return administration.Broadcast(command.Argument);

            case "digest":
                // Sends the digest as its own message rather than returning it, because
                // it also closes the week — the reply is the acknowledgement, and the
                // digest itself goes through the ordinary queue.
                sendDigest();
                return new OperatorMessage.DigestRequested();

            case "help":
            case "start":
                return new OperatorMessage.Help();

            default:
                return new OperatorMessage.CommandRejected(CommandFault.Unknown);
        }
    }
}

/// <summary>
/// Listens for commands from the operator's chat and carries them out.
/// <para>
/// <b>The bot token stops being a write-only credential here.</b> Up to this point it
/// could only send messages; from here it can change who is allowed to sign in. Three
/// things follow from that, and all three are in this loop rather than in a document:
/// </para>
/// <list type="bullet">
/// <item>
/// The sender's chat id is checked against configuration on <b>every</b> update, not once
/// at startup. Anybody can find a bot and message it; being in the chat is not a property
/// the Bot API enforces for us.
/// </item>
/// <item>
/// The update cursor is persisted, so a restart does not replay a day of commands. For a
/// channel that can deny accounts, replaying is not a cosmetic problem.
/// </item>
/// <item>
/// It is off unless <c>Telegram:Commands</c> is set. A hub that only reports leaves the
/// token unable to do anything but talk, which is the right default.
/// </item>
/// </list>
/// </summary>
public sealed class OperatorCommandService(
    IOperatorUpdateSource updates,
    IOperatorNotifier notifier,
    IHubAdministration administration,
    WeeklyDigestService digest,
    OperatorStateStore store,
    IOptions<OperatorChannelOptions> options,
    TimeProvider time,
    ILogger<OperatorCommandService> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a failed poll.
    /// <para>
    /// The poll itself blocks for thirty seconds at Telegram's end, so a healthy loop
    /// needs no delay at all. This is only for when the call fails immediately — no
    /// network, or a rejected token — where an undelayed loop would spin.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    private readonly OperatorChannelOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CommandsEnabled)
        {
            return;
        }

        long offset = store.Read(state => state.UpdateOffset);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<OperatorUpdate> batch =
                    await updates.PollAsync(offset, stoppingToken).ConfigureAwait(false);

                foreach (OperatorUpdate update in batch)
                {
                    // Advanced whether or not the update is acted on. An update from a
                    // stranger that was never acknowledged would be redelivered forever.
                    offset = Math.Max(offset, update.UpdateId + 1);

                    Handle(update);
                }

                if (batch.Count > 0)
                {
                    store.Mutate(state => state.UpdateOffset = offset);
                    store.Flush();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Polling for operator commands failed.");

                try
                {
                    await Task.Delay(Backoff, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Handle(OperatorUpdate update)
    {
        // The whole of the authorisation for this interface. A bot's username is public
        // and anybody can start a chat with it, so "it arrived at our bot" proves
        // nothing — only the chat id does.
        if (!string.Equals(update.ChatId, _options.ChatId, StringComparison.Ordinal))
        {
            // Not answered. A reply would confirm the bot is live and attached to a real
            // hub, which is the one thing an unknown sender learns nothing about
            // otherwise. Logged without the text, which is a stranger's input.
            logger.LogWarning("Ignored a Telegram message from an unconfigured chat.");
            return;
        }

        OperatorCommand command = OperatorCommand.Parse(update.Text);

        if (!command.IsCommand)
        {
            return;
        }

        try
        {
            notifier.Send(OperatorCommands.Execute(command, administration, digest.Send));
        }
        catch (Exception error)
        {
            // One bad command must not end the loop. The operator would be left with a
            // bot that answered until the moment they needed it.
            logger.LogWarning(error, "An operator command failed.");
            notifier.Send(new OperatorMessage.CommandRejected(CommandFault.Unavailable));
        }
    }
}
