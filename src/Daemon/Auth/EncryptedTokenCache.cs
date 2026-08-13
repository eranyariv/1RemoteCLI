using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace OneRemoteCli.Daemon.Auth;

/// <summary>
/// Persists MSAL's token cache to disk, encrypted with DPAPI under
/// <see cref="DataProtectionScope.CurrentUser"/>.
/// <para>
/// CurrentUser scope is the whole point: the blob can only be decrypted by the
/// Windows account that wrote it, so another user on the same machine — including a
/// local administrator reading the file directly — gets ciphertext. This works
/// precisely *because* the agent runs as the interactive user. A LocalSystem
/// service could not do this, which is one of the reasons that design was rejected.
/// </para>
/// <para>
/// The agent never sees a refresh token. It asks MSAL for an access token and lets
/// MSAL decide whether to renew; the only thing this class handles is an opaque,
/// already-encrypted blob.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedTokenCache
{
    /// <summary>
    /// Ties the ciphertext to this application. A blob lifted from another program's
    /// DPAPI store will not decrypt here, and vice versa.
    /// </summary>
    private static readonly byte[] Entropy = "1RemoteCLI/msal-cache/v1"u8.ToArray();

    private readonly string _path;
    private readonly object _fileLock = new();

    public EncryptedTokenCache(string? path = null)
    {
        _path = path ?? AuthConfig.CachePath;
    }

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Hooks this cache up to a client. Call once, before any token call.</summary>
    public void Bind(ITokenCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        cache.SetBeforeAccess(OnBeforeAccess);
        cache.SetAfterAccess(OnAfterAccess);
    }

    /// <summary>Deletes the cache. Used by <c>1remote logout</c>.</summary>
    public void Clear()
    {
        lock (_fileLock)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        if (Read() is byte[] plaintext)
        {
            try
            {
                args.TokenCache.DeserializeMsalV3(plaintext);
            }
            catch (MsalClientException)
            {
                // A cache from an older MSAL format. Not recoverable, not worth
                // failing over: the cost is one interactive sign-in.
                Clear();
            }
        }
    }

    /// <summary>
    /// Decrypts the cache file, or returns null when there is nothing usable.
    /// <para>
    /// An unreadable file is deleted rather than reported. It means the blob was
    /// written by another Windows account or is truncated, and neither can be
    /// recovered — but both would otherwise break every future sign-in silently.
    /// </para>
    /// </summary>
    internal byte[]? Read()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                return ProtectedData.Unprotect(
                    File.ReadAllBytes(_path),
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                File.Delete(_path);
                return null;
            }
        }
    }

    /// <summary>Encrypts and stores the cache.</summary>
    internal void Write(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        lock (_fileLock)
        {
            byte[] ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            // Through a temp file: a half-written cache would cost the user a
            // sign-in for no reason, and DPAPI cannot decrypt a truncated blob.
            string temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, ciphertext);
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (args.HasStateChanged)
        {
            Write(args.TokenCache.SerializeMsalV3());
        }
    }
}
