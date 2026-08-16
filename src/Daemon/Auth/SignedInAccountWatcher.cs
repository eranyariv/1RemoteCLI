using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneRemoteCli.Daemon.Auth;

/// <summary>
/// Watches the token cache and reports when the signed-in account becomes a
/// different one — including becoming nobody.
/// <para>
/// Signing in and out happen in short-lived <c>1remote login</c> and
/// <c>1remote logout</c> processes, while the agent runs for weeks. The file is the
/// only thing the two share, so without watching it the agent would carry on
/// relaying for an account the user believes they have signed out of, until
/// something unrelated dropped the connection.
/// </para>
/// <para>
/// It reports a change of <em>account</em> rather than a change of file, and the
/// distinction is the entire design. MSAL rewrites the cache every time it renews an
/// access token — roughly hourly, forever — and treating that as a credential change
/// would drop and rebuild the hub connection every hour for no reason, interrupting
/// whoever was watching from their phone.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SignedInAccountWatcher : IDisposable
{
    /// <summary>
    /// How long to let the file settle. One save arrives as several events — a write
    /// to the temporary file, then the rename over the top — and reading halfway
    /// through is a wasted decrypt.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly Func<CancellationToken, Task<SignedInAccount?>> _readAccount;
    private readonly Func<SignedInAccount?, Task> _onChanged;
    private readonly ILogger _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _settling;

    private SignedInAccount? _account;
    private bool _disposed;

    public SignedInAccountWatcher(
        string cachePath,
        Func<CancellationToken, Task<SignedInAccount?>> readAccount,
        Func<SignedInAccount?, Task> onChanged,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(readAccount);
        ArgumentNullException.ThrowIfNull(onChanged);

        _readAccount = readAccount;
        _onChanged = onChanged;
        _logger = logger ?? NullLogger.Instance;

        string directory = Path.GetDirectoryName(Path.GetFullPath(cachePath))!;

        // Created up front: there is no cache directory until the first sign-in, and a
        // watcher cannot be pointed at a directory that does not exist yet.
        Directory.CreateDirectory(directory);

        _settling = new System.Threading.Timer(_ => _ = CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(cachePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;

        // A watcher that dies takes silent sign-outs with it, so say so rather than
        // leaving the agent looking healthy.
        _watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Watching the token cache failed");
    }

    /// <summary>
    /// Records who is signed in now, then starts watching.
    /// <para>
    /// Priming first is what stops the agent announcing a change the moment it starts:
    /// the first event has to be compared against something, and the account that was
    /// already there is not news.
    /// </para>
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _account = await ReadQuietlyAsync(cancellationToken).ConfigureAwait(false);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Who was signed in as of the last look.</summary>
    public SignedInAccount? Account => _account;

    /// <summary>
    /// Raised after <see cref="Account"/> becomes somebody else. For observers that
    /// only want to redraw; the constructor's callback is the one that has to happen.
    /// </summary>
    public event Action? Changed;

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _settling.Change(Settle, Timeout.InfiniteTimeSpan);

    /// <summary>Re-reads the account and reports it only if it is somebody else.</summary>
    internal async Task CheckAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_disposed)
            {
                return;
            }

            SignedInAccount? current = await ReadQuietlyAsync(CancellationToken.None).ConfigureAwait(false);

            if (string.Equals(current?.Id, _account?.Id, StringComparison.Ordinal))
            {
                return;
            }

            _account = current;

            // Before the callback, so an observer redrawing on this already sees the
            // new account rather than the one being replaced.
            Changed?.Invoke();

            await _onChanged(current).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failure here must not take the agent down: it runs on a timer callback,
            // where an escaping exception ends the process.
            _logger.LogWarning(ex, "Reacting to a change of signed-in account failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SignedInAccount?> ReadQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _readAccount(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A cache being rewritten underneath us reads as a failure. Treating that
            // as "signed out" would drop the connection on a passing race.
            _logger.LogDebug(ex, "Reading the signed-in account failed");
            return _account;
        }
    }

    public void Dispose()
    {
        _disposed = true;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _settling.Dispose();
        _gate.Dispose();
    }
}
