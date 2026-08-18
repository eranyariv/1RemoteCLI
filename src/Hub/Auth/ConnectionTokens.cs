using System.Collections.Concurrent;
using System.Security.Claims;

namespace OneRemoteCli.Hub.Auth;

/// <summary>
/// Remembers when each live connection's token expires, and ends the ones that let it
/// pass without refreshing.
/// <para>
/// <b>This exists because SignalR authenticates once.</b> The token is checked during
/// the handshake and never again, so a WebSocket outlives its access token for as long
/// as it stays open — which for a phone left attached is days. Revoking someone's
/// access, or removing them from the allowlist, would have no effect on the connection
/// they already have. That is the gap this closes.
/// </para>
/// <para>
/// Sweeping on a timer rather than arming one timer per connection: the work is
/// trivially small, the resolution needed is minutes, and a timer per connection is a
/// leak waiting for the one disposal path somebody forgets.
/// </para>
/// </summary>
public sealed class ConnectionTokens(TimeProvider time)
{
    /// <summary>
    /// How far ahead of expiry the holder is asked to refresh.
    /// <para>
    /// Five minutes is comfortably longer than a token acquisition takes even when it
    /// has to go all the way to Entra on a bad connection, and short enough that the
    /// hub is not warning about a token that most refreshes would have replaced
    /// anyway.
    /// </para>
    /// </summary>
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long past expiry a connection is left alone before it is aborted.
    /// <para>
    /// The same skew the handshake allows, and for the same reason. Being stricter here
    /// than at admission would let the hub accept a token and then immediately kill the
    /// connection it just accepted — the sort of inconsistency that presents as a
    /// connection that drops for no reason on a machine whose clock is slightly off.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Grace = EntraTokenValidation.ClockSkew;

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How many connections are being watched. For tests and diagnostics.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Starts watching a connection.
    /// <para>
    /// A token with no readable expiry is not tracked rather than tracked as
    /// "expires now". Admission has already decided this token is genuine, and
    /// disconnecting somebody over a claim we could not parse would turn a hub-side
    /// misunderstanding into their outage.
    /// </para>
    /// </summary>
    public void Track(string connectionId, string userKey, DateTimeOffset? expiresAt, Action abort)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentException.ThrowIfNullOrEmpty(userKey);
        ArgumentNullException.ThrowIfNull(abort);

        if (expiresAt is null) return;

        _entries[connectionId] = new Entry(userKey, expiresAt.Value, abort, Warned: false);
    }

    public void Forget(string connectionId) => _entries.TryRemove(connectionId, out _);

    /// <summary>The user this connection was admitted as, or null if it is not tracked.</summary>
    public string? UserKeyOf(string connectionId) =>
        _entries.TryGetValue(connectionId, out Entry? entry) ? entry.UserKey : null;

    /// <summary>
    /// Records a fresh token for a connection, clearing the warning so a later expiry
    /// is warned about again.
    /// </summary>
    public void Renew(string connectionId, DateTimeOffset expiresAt)
    {
        // Not an upsert. A connection the sweeper has already given up on must not be
        // able to reinstate itself, and a refresh arriving for a connection that never
        // registered one is a bug we would rather not paper over.
        if (!_entries.TryGetValue(connectionId, out Entry? entry)) return;

        _entries[connectionId] = entry with { ExpiresAt = expiresAt, Warned = false };
    }

    /// <summary>
    /// Aborts every connection whose token has run out, and returns the connections
    /// that should be asked to refresh.
    /// <para>
    /// Aborting here rather than returning a list to abort: the caller would have no
    /// way to do it atomically, and a connection that is told to refresh in the same
    /// pass in which it is killed would produce a confusing pair of events.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Sweep()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<string> warn = [];

        foreach ((string connectionId, Entry entry) in _entries)
        {
            if (now >= entry.ExpiresAt + Grace)
            {
                // Removed first. Abort will raise OnDisconnectedAsync, which calls
                // Forget, and a second pass finding the same entry would abort twice.
                if (_entries.TryRemove(connectionId, out _))
                {
                    entry.Abort();
                }

                continue;
            }

            if (entry.Warned || now < entry.ExpiresAt - WarnBefore) continue;

            // Marked before the notification is sent, not after. A send that fails is
            // a connection that is about to be aborted anyway; retrying the warning
            // every sweep until then would only add noise.
            if (_entries.TryUpdate(connectionId, entry with { Warned = true }, entry))
            {
                warn.Add(connectionId);
            }
        }

        return warn;
    }

    /// <summary>When this connection's token runs out, for tests and diagnostics.</summary>
    public DateTimeOffset? ExpiryOf(string connectionId) =>
        _entries.TryGetValue(connectionId, out Entry? entry) ? entry.ExpiresAt : null;

    /// <summary>
    /// Ends every live connection belonging to one account, and reports how many.
    /// <para>
    /// What <c>/kick</c> and <c>/deny</c> are for. Removing somebody from the allowlist
    /// stops them reconnecting and stops their next token refresh, but a socket they
    /// already hold would survive until its token expired — up to an hour for a phone
    /// left attached. If revocation is worth having from a phone, it has to take effect
    /// while the operator is still looking at it.
    /// </para>
    /// <para>
    /// Removed before aborting, for the same reason <see cref="Sweep"/> does it in that
    /// order: the abort raises <c>OnDisconnectedAsync</c>, which calls
    /// <see cref="Forget"/>, and an entry still present would be aborted twice.
    /// </para>
    /// </summary>
    public int AbortAllFor(string userKey)
    {
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return 0;
        }

        int closed = 0;

        foreach ((string connectionId, Entry entry) in _entries)
        {
            if (!string.Equals(entry.UserKey, userKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (_entries.TryRemove(connectionId, out _))
            {
                entry.Abort();
                closed++;
            }
        }

        return closed;
    }

    private sealed record Entry(string UserKey, DateTimeOffset ExpiresAt, Action Abort, bool Warned);
}

/// <summary>Reads the standard <c>exp</c> claim off a validated principal.</summary>
public static class TokenExpiry
{
    public const string ExpiryClaim = "exp";

    public static DateTimeOffset? Of(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? value = principal.FindFirst(ExpiryClaim)?.Value
            ?? principal.FindFirst(ClaimTypes.Expiration)?.Value;

        return value is not null
            && long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
