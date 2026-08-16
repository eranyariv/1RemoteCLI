using System.Runtime.Versioning;
using System.Text;
using Microsoft.Identity.Client;
using OneRemoteCli.Daemon.Auth;

namespace OneRemoteCli.Daemon.Tests;

[SupportedOSPlatform("windows")]
public class EncryptedTokenCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"1remote-cache-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_directory, "msal.cache");

    [Fact]
    public void RoundTripsTheCacheThroughDisk()
    {
        var cache = new EncryptedTokenCache(CachePath);
        byte[] payload = Encoding.UTF8.GetBytes("{\"AccessToken\":{}}");

        cache.Write(payload);

        Assert.True(cache.Exists);
        Assert.Equal(payload, new EncryptedTokenCache(CachePath).Read());
    }

    /// <summary>
    /// The whole point of the cache is that the token never sits on disk in the
    /// clear, so this asserts the file does not contain what was written.
    /// </summary>
    [Fact]
    public void NeverWritesTheTokenInTheClear()
    {
        var cache = new EncryptedTokenCache(CachePath);
        cache.Write(Encoding.UTF8.GetBytes("secret-refresh-token"));

        byte[] onDisk = File.ReadAllBytes(CachePath);

        Assert.DoesNotContain("secret-refresh-token", Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-refresh-token", Encoding.Unicode.GetString(onDisk), StringComparison.Ordinal);
    }

    /// <summary>
    /// A blob another Windows account wrote cannot be decrypted here — that is the
    /// security property. It must show up as "sign in again", not as a crash on
    /// every agent start forever.
    /// </summary>
    [Fact]
    public void DiscardsACacheItCannotDecrypt()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(CachePath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var cache = new EncryptedTokenCache(CachePath);

        Assert.Null(cache.Read());
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void ReadsNothingWhenThereIsNoCache()
    {
        Assert.Null(new EncryptedTokenCache(CachePath).Read());
        Assert.False(new EncryptedTokenCache(CachePath).Exists);
    }

    [Fact]
    public void ClearRemovesTheCacheFile()
    {
        var cache = new EncryptedTokenCache(CachePath);
        cache.Write([1, 2, 3]);

        cache.Clear();

        Assert.False(File.Exists(CachePath));
        cache.Clear(); // Idempotent: logging out twice is not an error.
    }

    /// <summary>
    /// Encryption is bound to this application, so a blob lifted from another
    /// program's DPAPI store is rejected even though the user could decrypt it.
    /// </summary>
    [Fact]
    public void RejectsABlobEncryptedWithoutOurEntropy()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(
            CachePath,
            System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes("someone else's cache"),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

        Assert.Null(new EncryptedTokenCache(CachePath).Read());
    }

    /// <summary>The cache is machine-local state, so it belongs beside the identity.</summary>
    [Fact]
    public void LivesUnderLocalAppData()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "1RemoteCLI",
                "msal.cache"),
            AuthConfig.CachePath);
    }

    /// <summary>
    /// Binding must survive a client that has never seen a cache file, since that is
    /// the first-run path.
    /// </summary>
    [Fact]
    public async Task BindsToAnMsalClientWithoutAnExistingFile()
    {
        IPublicClientApplication client = PublicClientApplicationBuilder
            .Create(AuthConfig.ClientId)
            .WithAuthority(AuthConfig.Authority)
            .WithRedirectUri(AuthConfig.RedirectUri)
            .Build();

        new EncryptedTokenCache(CachePath).Bind(client.UserTokenCache);

        Assert.Empty(await client.GetAccountsAsync());
    }

    /// <summary>
    /// The agent runs for weeks; `1remote login` and `1remote logout` are separate
    /// short-lived processes writing the same file. So a client that has already read
    /// an account has to notice when the file it came from is deleted — otherwise
    /// signing out leaves the agent connected and the machine still visible from the
    /// phone, which is the whole of issue #60.
    /// </summary>
    [Fact]
    public async Task ForgetsAnAccountWhenAnotherProcessSignsOut()
    {
        var cache = new EncryptedTokenCache(CachePath);
        cache.Write(Encoding.UTF8.GetBytes(CacheHolding("someone@example.com", "uid")));

        IPublicClientApplication client = NewClient();
        cache.Bind(client.UserTokenCache);

        IAccount account = Assert.Single(await client.GetAccountsAsync());
        Assert.Equal("someone@example.com", account.Username);

        // What `1remote logout` does, from a process this client knows nothing about.
        new EncryptedTokenCache(CachePath).Clear();

        Assert.Empty(await client.GetAccountsAsync());
    }

    /// <summary>
    /// Signing in as somebody else must replace the account, not add a second one.
    /// A merged cache leaves <c>GetAccountsAsync().FirstOrDefault()</c> choosing
    /// between two identities by whatever order MSAL happens to return, so the agent
    /// can carry on as the old account after a successful sign-in as the new one.
    /// </summary>
    [Fact]
    public async Task ReplacesTheAccountWhenAnotherProcessSignsInAsSomebodyElse()
    {
        var cache = new EncryptedTokenCache(CachePath);
        cache.Write(Encoding.UTF8.GetBytes(CacheHolding("first@example.com", "uidone")));

        IPublicClientApplication client = NewClient();
        cache.Bind(client.UserTokenCache);

        Assert.Equal("first@example.com", Assert.Single(await client.GetAccountsAsync()).Username);

        new EncryptedTokenCache(CachePath).Write(Encoding.UTF8.GetBytes(CacheHolding("second@example.com", "uidtwo")));

        IAccount now = Assert.Single(await client.GetAccountsAsync());
        Assert.Equal("second@example.com", now.Username);
    }

    private static IPublicClientApplication NewClient() => PublicClientApplicationBuilder
        .Create(AuthConfig.ClientId)
        .WithAuthority(AuthConfig.Authority)
        .WithRedirectUri(AuthConfig.RedirectUri)
        .Build();

    /// <summary>
    /// A serialised MSAL v3 cache holding one account and nothing else. Hand-built
    /// rather than captured from a real sign-in, because a real one carries a live
    /// refresh token that would have to be redacted anyway.
    /// <para>
    /// The refresh-token entry is not optional padding. MSAL derives accounts from the
    /// refresh tokens it holds and joins them to the account records, so an account on
    /// its own is invisible to <c>GetAccountsAsync</c>.
    /// </para>
    /// <para>
    /// <paramref name="id"/> has to differ between two people. Two blobs sharing a
    /// home account id overwrite each other even when the cache is merged rather than
    /// replaced, which makes a switch-account test pass against code that cannot
    /// actually switch accounts.
    /// </para>
    /// </summary>
    private static string CacheHolding(string username, string id) => $$"""
        {
          "Account": {
            "{{id}}.utid-login.windows.net-utid": {
              "home_account_id": "{{id}}.utid",
              "environment": "login.windows.net",
              "realm": "utid",
              "local_account_id": "{{id}}",
              "username": "{{username}}",
              "authority_type": "MSSTS"
            }
          },
          "RefreshToken": {
            "{{id}}.utid-login.windows.net-refreshtoken-{{AuthConfig.ClientId}}--": {
              "home_account_id": "{{id}}.utid",
              "environment": "login.windows.net",
              "client_id": "{{AuthConfig.ClientId}}",
              "credential_type": "RefreshToken",
              "secret": "not-a-real-token"
            }
          }
        }
        """;

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public class AuthConfigTests
{
    /// <summary>
    /// `common` rather than a tenant id: a personal Microsoft account and a work
    /// account must both work, and the user should not have to know which they have.
    /// </summary>
    [Fact]
    public void SignsInAgainstTheCommonAuthority()
    {
        Assert.EndsWith("/common", AuthConfig.Authority, StringComparison.Ordinal);
    }

    [Fact]
    public void AsksForOurOwnApiScope()
    {
        Assert.Equal($"api://{AuthConfig.ClientId}/Session.Access", Assert.Single(AuthConfig.Scopes));
    }

    /// <summary>
    /// Loopback, not device code: the user is at the machine, and device code flow is
    /// blocked by Conditional Access in many tenants.
    /// <para>
    /// The host has to be <c>127.0.0.1</c> rather than <c>localhost</c>. This
    /// registration is shared with the PWA, whose dev servers are registered as SPA
    /// redirects on <c>localhost</c>; Entra matches loopback redirects without regard
    /// to port, so a <c>localhost</c> redirect here would also match those and get the
    /// authorization code typed as single-page, failing every sign-in with
    /// <c>AADSTS90023</c>. Asserted because the two spellings look interchangeable and
    /// are not.
    /// </para>
    /// </summary>
    [Fact]
    public void UsesTheLoopbackRedirect()
    {
        Assert.Equal("http://127.0.0.1", AuthConfig.RedirectUri);
    }
}
