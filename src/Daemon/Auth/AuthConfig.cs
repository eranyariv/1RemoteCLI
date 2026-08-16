namespace OneRemoteCli.Daemon.Auth;

/// <summary>
/// The Entra application this machine signs in as.
/// <para>
/// None of this is secret. The client id, authority and scope ship in the PWA
/// bundle too, and the agent is a public client with no credential at all: it uses
/// the loopback redirect with PKCE. There is deliberately nothing in this project
/// that has to be kept out of git — a design constraint, not an accident.
/// </para>
/// <para>
/// The agent and the PWA share one registration on purpose. Because both sides
/// present a token from the same application, "the phone and the machine are the
/// same person" becomes something the hub can verify rather than infer.
/// </para>
/// </summary>
public static class AuthConfig
{
    public const string ClientId = "3db435ae-5e69-483c-a044-d6e8b6262fc6";

    /// <summary>
    /// <c>common</c>, not a tenant id: this has to work for a personal Microsoft
    /// account and for any work account, and the user should not have to know which
    /// kind theirs is.
    /// </summary>
    public const string Authority = "https://login.microsoftonline.com/common";

    /// <summary>The scope the hub checks for. Our own API, not Graph.</summary>
    public const string ApiScope = $"api://{ClientId}/Session.Access";

    public static readonly string[] Scopes = [ApiScope];

    /// <summary>
    /// Loopback with an ephemeral port. MSAL picks the port and matches it against the
    /// registered <c>http://127.0.0.1</c> redirect.
    /// <para>
    /// <c>127.0.0.1</c> rather than <c>localhost</c>, and the difference is not
    /// cosmetic. This registration is shared with the PWA, which needs its dev and
    /// preview servers registered under the SPA platform. Entra matches loopback
    /// redirects without regard to port, so a CLI redirect of
    /// <c>http://localhost:{ephemeral}</c> also matches those SPA entries — and SPA
    /// classification wins. The authorization code then comes back marked as
    /// single-page, redeemable only with an <c>Origin</c> header that a desktop client
    /// does not send, and every sign-in fails with <c>AADSTS90023</c>.
    /// </para>
    /// <para>
    /// The two host spellings are distinct strings, so this one cannot collide with a
    /// <c>localhost</c> SPA entry, and both platforms coexist on one registration.
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
