using System.Security.Claims;

namespace OneRemoteCli.Hub.Auth;

/// <summary>Why a connection was refused. Distinguishable on purpose.</summary>
public enum AccessDecision
{
    Allowed,

    /// <summary>The token validated but carries no <c>tid</c>/<c>oid</c> pair.</summary>
    NoUserKey,

    /// <summary>The token does not carry <c>Session.Access</c>.</summary>
    MissingScope,

    /// <summary>A real, valid identity that is simply not on the list.</summary>
    NotAllowlisted,
}

/// <summary>The outcome of an admission check, with enough detail to log usefully.</summary>
/// <param name="Decision">Whether the caller gets in, and if not, why.</param>
/// <param name="Key">The resolved user key, when there was one.</param>
/// <param name="Username">The token's preferred_username, when present.</param>
public sealed record AccessResult(AccessDecision Decision, string? Key, string? Username)
{
    public bool IsAllowed => Decision == AccessDecision.Allowed;

    public string Reason => Decision switch
    {
        AccessDecision.Allowed => "allowed",
        AccessDecision.NoUserKey => "the token is missing tid or oid",
        AccessDecision.MissingScope => "the token does not carry the Session.Access scope",
        AccessDecision.NotAllowlisted => $"'{Key ?? Username}' is not on this hub's allowlist",
        _ => "unknown",
    };
}

/// <summary>
/// Decides who may connect. Separate from token validation because they answer
/// different questions: validation asks "is this really who it claims to be", and
/// this asks "should that person be here at all".
/// <para>
/// Configuration is the base, and it can be amended at runtime by the operator channel's
/// <c>/allow</c> and <c>/deny</c>. Those amendments are held here and persisted
/// elsewhere, so they survive a restart: a <c>/deny</c> that came back at the next
/// restart because the account is still in App Service configuration would be worse than
/// having no command at all, because the operator would believe a compromised account had
/// been dealt with.
/// </para>
/// <para>
/// Reads happen on every handshake and every token refresh; writes happen when a human
/// sends a message. So the list is an immutable snapshot swapped under a lock, and
/// <see cref="Check"/> never takes one.
/// </para>
/// </summary>
public sealed class AccountAllowlist
{
    private readonly object _gate = new();

    /// <summary>What configuration said. Kept so a runtime denial can be lifted back to it.</summary>
    private readonly HashSet<string> _configured;

    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    private volatile Snapshot _snapshot;

    public AccountAllowlist(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _configured = new HashSet<string>(
            entries.Where(entry => !string.IsNullOrWhiteSpace(entry)).Select(entry => entry.Trim()),
            StringComparer.OrdinalIgnoreCase);

        _snapshot = Build();
    }

    /// <summary>True when nobody has been listed, which is treated as "let nobody in".</summary>
    public bool IsEmpty => _snapshot.Count == 0;

    public int Count => _snapshot.Count;

    /// <summary>
    /// Admits a validated principal, or explains why not.
    /// <para>
    /// An empty allowlist denies everyone rather than everyone in. A misconfigured
    /// hub that lets the world in is a far worse failure than one nobody can reach,
    /// and the second kind gets noticed and fixed within minutes.
    /// </para>
    /// </summary>
    public AccessResult Check(ClaimsPrincipal principal, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? key = UserKey.From(principal);
        string? username = UserKey.PreferredUsername(principal);

        if (key is null)
        {
            return new AccessResult(AccessDecision.NoUserKey, null, username);
        }

        if (!UserKey.HasScope(principal, requiredScope))
        {
            return new AccessResult(AccessDecision.MissingScope, key, username);
        }

        Snapshot snapshot = _snapshot;

        // Denial is checked first and wins over everything, including configuration.
        // Revoking access has to be immediate to be worth having.
        bool allowed =
            !Snapshot.Matches(snapshot.DeniedKeys, snapshot.DeniedUsernames, key, username) &&
            Snapshot.Matches(snapshot.Keys, snapshot.Usernames, key, username);

        return new AccessResult(
            allowed ? AccessDecision.Allowed : AccessDecision.NotAllowlisted,
            key,
            username);
    }

    /// <summary>Whether this exact entry would be admitted today, without needing a token to ask with.</summary>
    public bool Contains(string entry)
    {
        string trimmed = (entry ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        Snapshot snapshot = _snapshot;

        return !snapshot.Denied.Contains(trimmed) && snapshot.Allowed.Contains(trimmed);
    }

    /// <summary>
    /// Admits an account at runtime. Returns false when it was already admitted.
    /// <para>
    /// Lifts a previous denial as well as adding, so <c>/deny</c> followed by
    /// <c>/allow</c> returns an account to where it started rather than leaving it denied
    /// by a rule that is no longer visible anywhere.
    /// </para>
    /// </summary>
    public bool Add(string entry)
    {
        string trimmed = (entry ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            bool changed = _denied.Remove(trimmed);
            changed |= !_configured.Contains(trimmed) && _added.Add(trimmed);

            if (changed)
            {
                _snapshot = Build();
            }

            return changed;
        }
    }

    /// <summary>Refuses an account at runtime, overriding configuration. Returns false when it was already refused.</summary>
    public bool Deny(string entry)
    {
        string trimmed = (entry ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            bool changed = _added.Remove(trimmed);
            changed |= _denied.Add(trimmed);

            if (changed)
            {
                _snapshot = Build();
            }

            return changed;
        }
    }

    /// <summary>The runtime amendments, so they can be written down and replayed after a restart.</summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> Denied) Amendments()
    {
        lock (_gate)
        {
            return ([.. _added], [.. _denied]);
        }
    }

    private Snapshot Build()
    {
        var allowed = new HashSet<string>(_configured, StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(_added);

        return new Snapshot(allowed, _denied);
    }

    /// <summary>
    /// One immutable view of the list, split the way a lookup needs it.
    /// <para>
    /// An '@' is what tells the two kinds apart: a user key is <c>{guid}:{guid}</c>, and
    /// an email cannot contain a colon before an '@'.
    /// </para>
    /// </summary>
    private sealed class Snapshot
    {
        public Snapshot(HashSet<string> allowed, HashSet<string> denied)
        {
            Allowed = allowed;
            Denied = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);

            (Keys, Usernames) = Split(allowed);
            (DeniedKeys, DeniedUsernames) = Split(Denied);

            Count = allowed.Count(entry => !Denied.Contains(entry));
        }

        public HashSet<string> Allowed { get; }

        public HashSet<string> Denied { get; }

        public HashSet<string> Keys { get; }

        public HashSet<string> Usernames { get; }

        public HashSet<string> DeniedKeys { get; }

        public HashSet<string> DeniedUsernames { get; }

        public int Count { get; }

        public static bool Matches(
            HashSet<string> keys,
            HashSet<string> usernames,
            string key,
            string? username) =>
            keys.Contains(key) || (username is not null && usernames.Contains(username));

        private static (HashSet<string> Keys, HashSet<string> Usernames) Split(IEnumerable<string> entries)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string entry in entries)
            {
                _ = entry.Contains('@', StringComparison.Ordinal) ? usernames.Add(entry) : keys.Add(entry);
            }

            return (keys, usernames);
        }
    }
}
