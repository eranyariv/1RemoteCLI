namespace OneRemoteCli.Hub.Auth;

/// <summary>
/// Everything the hub needs to decide whether a token is one of ours and whether
/// the person behind it is allowed in.
/// </summary>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";

    /// <summary>The application (client) id. Not a secret; it ships in the PWA bundle.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The scope a token must carry. Bare name, as it appears in <c>scp</c>.</summary>
    public string RequiredScope { get; set; } = "Session.Access";

    /// <summary>
    /// Who may connect at all.
    /// <para>
    /// This list is not optional. The app signs in against <c>common</c>, so anyone
    /// in the world with a Microsoft account can obtain a structurally valid token
    /// for it — a correctly validated token proves who someone is, not that they
    /// should be here. Without this list the hub would relay any stranger's phone to
    /// nothing, and every relay slot would be free to consume.
    /// </para>
    /// <para>
    /// Entries are either a <c>{tid}:{oid}</c> user key (preferred: immutable, and
    /// what the hub keys everything else on) or an email address matched against
    /// <c>preferred_username</c> (a convenience for onboarding someone before their
    /// oid is known — usernames are reassignable, so tighten these once the hub has
    /// logged the real key).
    /// </para>
    /// </summary>
    public IList<string> Allowlist { get; set; } = [];

    /// <summary>
    /// The audiences a token may name: the client id, and the same id as an
    /// <c>api://</c> URI. Entra issues one or the other depending on how the client
    /// asked, and both mean this application.
    /// </summary>
    public IEnumerable<string> ValidAudiences() => [ClientId, $"api://{ClientId}"];
}
