using System.Runtime.Versioning;
using Microsoft.Identity.Client;

namespace OneRemoteCli.Daemon.Auth;

/// <summary>What <c>1remote status</c> found.</summary>
/// <param name="Account">The signed-in account, or null when nobody is signed in.</param>
/// <param name="TokenValidUntil">When the cached access token expires, if one could be obtained silently.</param>
/// <param name="Problem">Why a silent token could not be obtained, when that is the reason.</param>
public sealed record AuthStatus(string? Account, DateTimeOffset? TokenValidUntil, string? Problem)
{
    public bool IsSignedIn => Account is not null;
}

/// <summary>
/// The one place the agent gets a token.
/// <para>
/// Sign-in uses the loopback redirect rather than device code, deliberately: the
/// user is standing at the machine, so the awkward "type this code on another
/// device" dance buys nothing — and device code flow is blocked outright by
/// Conditional Access in many tenants, which would make onboarding fail in exactly
/// the environments this has to work in.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenBroker
{
    private readonly IPublicClientApplication _client;
    private readonly EncryptedTokenCache _cache;

    public TokenBroker(EncryptedTokenCache? cache = null, IPublicClientApplication? client = null)
    {
        _cache = cache ?? new EncryptedTokenCache();

        _client = client ?? PublicClientApplicationBuilder
            .Create(AuthConfig.ClientId)
            .WithAuthority(AuthConfig.Authority)
            .WithRedirectUri(AuthConfig.RedirectUri)
            .Build();

        _cache.Bind(_client.UserTokenCache);
    }

    public string CachePath => _cache.Path;

    /// <summary>
    /// Signs in interactively through the system browser.
    /// <para>
    /// The system browser, not an embedded webview: it already holds the user's
    /// session and their passkeys or authenticator bindings, so most sign-ins are a
    /// single click — and an embedded webview asking for a Microsoft password is
    /// indistinguishable from a phishing page, which is a habit worth not teaching.
    /// </para>
    /// </summary>
    public async Task<AuthenticationResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        // Silent first: re-running `1remote login` when already signed in should be a
        // no-op, not a browser window.
        AuthenticationResult? silent = await TryAcquireSilentAsync(cancellationToken).ConfigureAwait(false);
        if (silent is not null)
        {
            return silent;
        }

        return await _client
            .AcquireTokenInteractive(AuthConfig.Scopes)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                HtmlMessageSuccess = SuccessPage,
                HtmlMessageError = ErrorPage,
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a token without any UI. This is what the agent uses; it must never
    /// prompt, because there may be nobody at the machine to answer.
    /// </summary>
    public async Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        AuthenticationResult? result = await TryAcquireSilentAsync(cancellationToken).ConfigureAwait(false);

        return result ?? throw new NotSignedInException();
    }

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        IAccount? account = (await _client.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account is null)
        {
            return new AuthStatus(null, null, null);
        }

        try
        {
            AuthenticationResult result = await _client
                .AcquireTokenSilent(AuthConfig.Scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return new AuthStatus(account.Username, result.ExpiresOn, null);
        }
        catch (MsalUiRequiredException ex)
        {
            // Signed in, but the refresh token is gone or a policy now demands the
            // user again. Reported rather than swallowed, because "signed in but
            // silently broken" is the state that wastes the most debugging time.
            return new AuthStatus(account.Username, null, ex.Message);
        }
    }

    /// <summary>Forgets the account and deletes the cache file.</summary>
    public async Task<bool> SignOutAsync()
    {
        IEnumerable<IAccount> accounts = await _client.GetAccountsAsync().ConfigureAwait(false);
        bool any = false;

        foreach (IAccount account in accounts)
        {
            await _client.RemoveAsync(account).ConfigureAwait(false);
            any = true;
        }

        // Belt and braces: MSAL rewrites the cache on removal, but the user asked for
        // the credential to be gone, so the file goes too.
        _cache.Clear();
        return any;
    }

    private async Task<AuthenticationResult?> TryAcquireSilentAsync(CancellationToken cancellationToken)
    {
        IAccount? account = (await _client.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account is null)
        {
            return null;
        }

        try
        {
            return await _client
                .AcquireTokenSilent(AuthConfig.Scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private const string SuccessPage = """
        <html><head><title>1RemoteCLI</title></head>
        <body style="font-family: system-ui; margin: 4rem; text-align: center">
        <h2>Signed in</h2><p>You can close this tab and go back to the terminal.</p>
        </body></html>
        """;

    private const string ErrorPage = """
        <html><head><title>1RemoteCLI</title></head>
        <body style="font-family: system-ui; margin: 4rem; text-align: center">
        <h2>Sign-in failed</h2><p>error: {0}</p><p>{1}</p>
        </body></html>
        """;
}

/// <summary>Thrown when a token is needed and nobody has signed in.</summary>
public sealed class NotSignedInException : Exception
{
    public NotSignedInException()
        : base("Not signed in. Run '1remote login' first.")
    {
    }
}
