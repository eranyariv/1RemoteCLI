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
    /// Loopback with an ephemeral port. MSAL picks the port and matches it against
    /// the registered <c>http://localhost</c> redirect.
    /// </summary>
    public const string RedirectUri = "http://localhost";

    private const string FolderName = "1RemoteCLI";

    /// <summary>Where the encrypted MSAL cache lives.</summary>
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName,
        "msal.cache");
}
