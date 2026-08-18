using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// Everything a command is allowed to do to the hub.
/// <para>
/// Every method returns an <see cref="OperatorMessage"/> — a member of the closed
/// vocabulary — rather than data a caller could format. There is no way to ask this for
/// a list of machines or sessions, because there is no method that returns one, and that
/// is the point: an administrative interface is exactly where "just show me which
/// machines" would feel like a reasonable next addition.
/// </para>
/// </summary>
public interface IHubAdministration
{
    OperatorMessage Status();

    OperatorMessage Health();

    OperatorMessage Version();

    OperatorMessage Allow(string account);

    OperatorMessage Deny(string account);

    OperatorMessage Kick(string account);

    OperatorMessage Broadcast(string text);
}

/// <summary>
/// The hub, as the operator's chat is permitted to see and change it.
/// <para>
/// This is where the chat stops being notifications and becomes an admin console. The
/// justification is that the alternative to <c>/allow</c> is opening the Azure portal on
/// a phone to edit an application setting and waiting for a restart, and the alternative
/// to <c>/deny</c> is doing that while an account is actively compromised. Both are
/// things that will simply not get done at the moment they matter.
/// </para>
/// <para>
/// Changes are made in memory and written to the state file in the same call, so they
/// take effect immediately and survive a restart. Configuration remains the base: this
/// amends it, and the amendments are visible in one small JSON file rather than being
/// invisible runtime state nobody can account for later.
/// </para>
/// </summary>
public sealed class HubAdministration(
    RelayRegistry registry,
    ConnectionTokens tokens,
    AccountAllowlist allowlist,
    PushSubscriptionStore pushSubscriptions,
    IPushBroadcaster broadcaster,
    OperatorStateStore store,
    IOptions<VapidOptions> vapid,
    TimeProvider time) : IHubAdministration
{
    private readonly RelayRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ConnectionTokens _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly AccountAllowlist _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
    private readonly PushSubscriptionStore _pushSubscriptions =
        pushSubscriptions ?? throw new ArgumentNullException(nameof(pushSubscriptions));
    private readonly IPushBroadcaster _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    private readonly OperatorStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly VapidOptions _vapid = vapid.Value;
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly DateTimeOffset _started = time.GetUtcNow();

    public OperatorMessage Status()
    {
        RelayCounts counts = _registry.Counts();

        return new OperatorMessage.StatusReport(
            counts.Machines,
            counts.Sessions,
            counts.Accounts,
            counts.Connections,
            Uptime,
            ProductVersion.Current);
    }

    public OperatorMessage Health() => new OperatorMessage.HealthReport(
        ProductVersion.Current,
        Uptime,
        _vapid.Configured,
        _allowlist.Count,
        _pushSubscriptions.UserCount);

    public OperatorMessage Version() => new OperatorMessage.VersionReport(ProductVersion.Current);

    public OperatorMessage Allow(string account)
    {
        string entry = (account ?? string.Empty).Trim();

        if (entry.Length == 0)
        {
            return new OperatorMessage.CommandRejected(CommandFault.MissingArgument);
        }

        if (!_allowlist.Add(entry))
        {
            return new OperatorMessage.CommandRejected(CommandFault.AlreadyDone);
        }

        Persist();

        return new OperatorMessage.AllowlistChanged(entry, Admitted: true, _allowlist.Count);
    }

    public OperatorMessage Deny(string account)
    {
        string entry = (account ?? string.Empty).Trim();

        if (entry.Length == 0)
        {
            return new OperatorMessage.CommandRejected(CommandFault.MissingArgument);
        }

        if (!_allowlist.Deny(entry))
        {
            return new OperatorMessage.CommandRejected(CommandFault.AlreadyDone);
        }

        Persist();

        // Removing them from the list stops the next handshake and the next refresh, but
        // a socket they already hold would live until its token expired. Denying without
        // closing would be revocation that takes up to an hour, announced as immediate.
        if (Resolve(entry) is { } userKey)
        {
            _tokens.AbortAllFor(userKey);
        }

        return new OperatorMessage.AllowlistChanged(entry, Admitted: false, _allowlist.Count);
    }

    public OperatorMessage Kick(string account)
    {
        string entry = (account ?? string.Empty).Trim();

        if (entry.Length == 0)
        {
            return new OperatorMessage.CommandRejected(CommandFault.MissingArgument);
        }

        if (Resolve(entry) is not { } userKey)
        {
            return new OperatorMessage.CommandRejected(CommandFault.NotFound);
        }

        return new OperatorMessage.AccountKicked(entry, _tokens.AbortAllFor(userKey));
    }

    public OperatorMessage Broadcast(string text)
    {
        string message = (text ?? string.Empty).Trim();

        if (message.Length == 0)
        {
            return new OperatorMessage.CommandRejected(CommandFault.MissingArgument);
        }

        if (!_vapid.Configured)
        {
            return new OperatorMessage.CommandRejected(CommandFault.Unavailable);
        }

        return new OperatorMessage.BroadcastSent(_broadcaster.Broadcast(message), message.Length);
    }

    private TimeSpan Uptime => _time.GetUtcNow() - _started;

    /// <summary>
    /// Turns whatever the operator typed into the user key the hub routes on.
    /// <para>
    /// An email is looked up against the accounts the hub has actually seen, because an
    /// address alone is not a routing identity — <c>preferred_username</c> is reassignable
    /// and nothing is keyed on it. Anything else is taken to be a <c>{tid}:{oid}</c> key
    /// already, which is the form the refusal alert hands the operator to paste.
    /// </para>
    /// </summary>
    private string? Resolve(string entry)
    {
        if (!entry.Contains('@', StringComparison.Ordinal))
        {
            return entry;
        }

        return _store.Read(state => state.Accounts
            .FirstOrDefault(account => string.Equals(account.Value, entry, StringComparison.OrdinalIgnoreCase))
            .Key);
    }

    /// <summary>
    /// Writes the amendments down immediately rather than waiting for the flush timer.
    /// <para>
    /// A denial is the one change here that must not be lost, and the reason to make one
    /// is often the reason the process is about to be restarted.
    /// </para>
    /// </summary>
    private void Persist()
    {
        (IReadOnlyList<string> added, IReadOnlyList<string> denied) = _allowlist.Amendments();

        _store.Mutate(state =>
        {
            state.Allowed = [.. added];
            state.Denied = [.. denied];
        });

        _store.Flush();
    }
}
