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
/// </summary>
public sealed class AccountAllowlist
{
    private readonly HashSet<string> _keys;
    private readonly HashSet<string> _usernames;

    public AccountAllowlist(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // An '@' is what tells the two apart: a user key is {guid}:{guid}, and an
        // email cannot contain a colon before an '@'.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()))
        {
            _ = entry.Contains('@', StringComparison.Ordinal) ? usernames.Add(entry) : keys.Add(entry);
        }

        _keys = keys;
        _usernames = usernames;
    }

    /// <summary>True when nobody has been listed, which is treated as "let nobody in".</summary>
    public bool IsEmpty => _keys.Count == 0 && _usernames.Count == 0;

    public int Count => _keys.Count + _usernames.Count;

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

        bool allowed = _keys.Contains(key)
            || (username is not null && _usernames.Contains(username));

        return new AccessResult(
            allowed ? AccessDecision.Allowed : AccessDecision.NotAllowlisted,
            key,
            username);
    }
}
