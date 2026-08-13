using System.Security.Claims;

namespace OneRemoteCli.Hub.Auth;

/// <summary>
/// Turns a validated token into the single string the hub uses to mean "this person".
/// </summary>
public static class UserKey
{
    /// <summary>Entra claim names, as they appear in a v2.0 access token.</summary>
    public const string TenantIdClaim = "tid";

    public const string ObjectIdClaim = "oid";

    public const string ScopeClaim = "scp";

    public const string PreferredUsernameClaim = "preferred_username";

    /// <summary>
    /// Builds <c>{tid}:{oid}</c>, or null when either claim is missing.
    /// <para>
    /// The tuple, not <c>oid</c> alone. Object ids are unique within a tenant, not
    /// across tenants, and this app accepts every tenant — so <c>oid</c> on its own
    /// would eventually let one tenant's user collide with another's and inherit
    /// their machines. This is also Microsoft's documented guidance for multi-tenant
    /// apps.
    /// </para>
    /// <para>
    /// <c>sub</c> is deliberately unused. It is pairwise: a different value per
    /// application <em>and</em> per tenant, which makes it useless as a stable key
    /// across anything, including our own agent and PWA if they ever diverge.
    /// </para>
    /// </summary>
    public static string? From(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? tenantId = Find(principal, TenantIdClaim);
        string? objectId = Find(principal, ObjectIdClaim);

        return string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)
            ? null
            : $"{tenantId}:{objectId}";
    }

    /// <summary>True when the token carries the scope the hub requires.</summary>
    public static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // scp is a single space-delimited string, not repeated claims. Splitting is
        // required: a substring match would let "Session.AccessSomethingElse" pass.
        string? scopes = Find(principal, ScopeClaim);

        return scopes is not null
            && scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(requiredScope, StringComparer.Ordinal);
    }

    public static string? PreferredUsername(ClaimsPrincipal principal) =>
        Find(principal, PreferredUsernameClaim);

    /// <summary>
    /// Reads a claim by its short name or by the SOAP-era URI ASP.NET may map it to.
    /// </summary>
    private static string? Find(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value
        ?? principal.FindFirst($"http://schemas.microsoft.com/identity/claims/{type}")?.Value;
}
