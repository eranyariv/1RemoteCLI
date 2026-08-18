using System.Security.Claims;
using Microsoft.Identity.Client;

namespace OneRemoteCli.Daemon.Auth;

/// <summary>
/// Finding the human half of "who is signed in", and pairing it with the email.
/// <para>
/// A UPN on its own is a weak answer to "which account is this?" — the case worth
/// worrying about is a browser already signed in to a work account quietly supplying
/// it, and <c>e.yariv@contoso.onmicrosoft.com</c> looks no more or less plausible
/// than the personal account the user meant to use. A display name is what people
/// actually recognise.
/// </para>
/// <para>
/// The name is read from the <em>cached</em> ID token rather than from a fresh token
/// response, because the agent's normal start is from cache and possibly offline, and
/// an identity that only appears once the network comes back is one users would learn
/// not to trust. It can still be missing — a cache written by a build older than this
/// one has no ID token claims to read — so every caller has to survive not having it.
/// </para>
/// </summary>
internal static class AccountName
{
    /// <summary>
    /// The claim both Entra and personal Microsoft accounts put a display name in.
    /// <para>
    /// Matched by its raw name: MSAL builds its principal straight from the token
    /// payload, so there is none of the URI claim-type mapping that
    /// <see cref="ClaimsPrincipal.Identity"/> would otherwise depend on.
    /// </para>
    /// </summary>
    private const string NameClaim = "name";

    /// <summary>The display name in an account's cached ID token, or null.</summary>
    public static string? Of(IAccount? account)
    {
        if (account is null)
        {
            return null;
        }

        try
        {
            IEnumerable<TenantProfile> profiles = account.GetTenantProfiles() ?? [];

            // Home tenant first. A guest carries one profile per tenant they have been
            // invited to, and the name a host directory chose for them is not the one
            // they know themselves by.
            return profiles
                .OrderByDescending(profile => profile.IsHomeTenant)
                .Select(profile => Of(profile.ClaimsPrincipal))
                .FirstOrDefault(name => name is not null);
        }
        catch (Exception ex) when (ex is MsalException or InvalidOperationException or NotSupportedException)
        {
            // Decoration on top of an identity that is already established. Nothing here
            // is worth failing a sign-in check over.
            return null;
        }
    }

    /// <summary>The display name in a freshly acquired token, or null.</summary>
    public static string? Of(ClaimsPrincipal? claims) => Clean(claims?.FindFirst(NameClaim)?.Value);

    /// <summary>
    /// Both halves when the name is known and adds something, the email alone when it
    /// is not.
    /// <para>
    /// "Adds something" is doing real work: plenty of directories set the display name
    /// to the UPN, and <c>eran@example.com (eran@example.com)</c> reads as a bug.
    /// </para>
    /// </summary>
    public static string Describe(string? displayName, string username)
    {
        string? name = Clean(displayName);

        return name is null || string.Equals(name, username, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{name} ({username})";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
