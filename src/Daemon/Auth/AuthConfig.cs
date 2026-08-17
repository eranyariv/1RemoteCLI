namespace OneRemoteCli.Daemon.Auth;

/// <summary>
/// The Entra applications this machine signs in with.
/// <para>
/// None of this is secret. The client ids, authority and scope ship in the PWA
/// bundle too, and the agent is a public client with no credential at all: it uses
/// the loopback redirect with PKCE. There is deliberately nothing in this project
/// that has to be kept out of git — a design constraint, not an accident.
/// </para>
/// <para>
/// Two registrations, one API. The agent signs in as its own native client and asks
/// for a scope owned by the <em>API</em> registration, which the phone app also
/// signs in as. So the token the hub receives has the same audience whichever
/// device produced it, and "the phone and the machine are the same person" stays
/// something the hub can verify — it checks the user, not the client.
/// </para>
/// </summary>
public static class AuthConfig
{
    /// <summary>
    /// The agent's own registration: native, loopback only, no SPA redirects.
    /// <para>
    /// Separate from <see cref="ApiClientId"/>, and it has to stay separate. The
    /// agent's redirect reaches Entra as <c>http://localhost:{ephemeral}</c> (MSAL
    /// rewrites loopback to <c>localhost</c> — see <see cref="RedirectUri"/>),
    /// loopback redirects match without regard to port, and where a request could
    /// match either platform SPA classification wins. While one registration carried
    /// both, the PWA's <c>http://localhost:5173/</c> dev entry captured the agent's
    /// redirect: the authorization code came back marked single-page, redeemable only
    /// with an <c>Origin</c> header a desktop client never sends, and every fresh
    /// sign-in died with <c>AADSTS90023</c>.
    /// </para>
    /// <para>
    /// Merging these two ids back together would look like tidying up and would
    /// bring that back. Keeping the platforms on separate registrations is what
    /// makes the collision impossible rather than merely unlikely.
    /// </para>
    /// </summary>
    public const string ClientId = "6a4e3951-3b1f-46f9-b20c-17bd30bf16f5";

    /// <summary>
    /// The registration that owns the API, and the one the PWA signs in as. This is
    /// the audience the hub validates.
    /// </summary>
    public const string ApiClientId = "3db435ae-5e69-483c-a044-d6e8b6262fc6";

    /// <summary>
    /// <c>common</c>, not a tenant id: this has to work for a personal Microsoft
    /// account and for any work account, and the user should not have to know which
    /// kind theirs is.
    /// </summary>
    public const string Authority = "https://login.microsoftonline.com/common";

    /// <summary>The scope the hub checks for. Our own API, not Graph.</summary>
    public const string ApiScope = $"api://{ApiClientId}/Session.Access";

    public static readonly string[] Scopes = [ApiScope];

    /// <summary>
    /// Loopback with an ephemeral port. MSAL picks the port.
    /// <para>
    /// What goes on the wire is <c>http://localhost:{port}</c> whichever loopback
    /// host is configured here — MSAL rewrites it — and <c>login.live.com</c> does
    /// not treat the two spellings as interchangeable. Both are registered on the
    /// agent's application so either survives an MSAL change of heart.
    /// </para>
    /// <para>
    /// That rewrite is the reason the <c>127.0.0.1</c> spelling never protected
    /// anything: the request always arrived looking like <c>localhost</c>, ready to
    /// be captured by a <c>localhost</c> SPA entry. Only the separate registration
    /// keeps that from happening — see <see cref="ClientId"/>.
    /// </para>
    /// </summary>
    public const string RedirectUri = "http://127.0.0.1";

    private const string FolderName = "1RemoteCLI";

    /// <summary>Where the encrypted MSAL cache lives.</summary>
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName,
        "msal.cache");
}
