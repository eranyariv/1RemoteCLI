using OneRemoteCli.Protocol;

namespace OneRemoteCli.Daemon.Update;

/// <summary>What the agent currently knows about newer releases.</summary>
public enum UpdateStage
{
    /// <summary>No check has completed since this agent started.</summary>
    NotChecked,

    /// <summary>The most recent check found that this is the current release.</summary>
    UpToDate,

    /// <summary>Asking github.com right now.</summary>
    Checking,

    /// <summary>There is a newer release, and nothing has been downloaded.</summary>
    Available,

    /// <summary>Downloading, checking and installing it.</summary>
    Installing,

    /// <summary>
    /// Installed, but the agent is still running the old build because sessions are
    /// open. See <see cref="UpdateService"/> on why it will not restart under them.
    /// </summary>
    Restart,

    /// <summary>The last check or update did not work, and <c>Message</c> says why.</summary>
    Failed,
}

/// <summary>
/// The update state, as one value.
/// <para>
/// A record rather than separate fields because it is written by a background loop and
/// read by the tray and the settings window on other threads: three separate writes can
/// be read back as a mixture of two updates, and "up to date, version 0.13 available"
/// is a combination that would make the window look broken.
/// </para>
/// </summary>
/// <param name="Stage">What is happening.</param>
/// <param name="Version">The newer release, without its <c>v</c>, when there is one.</param>
/// <param name="Message">What went wrong, for <see cref="UpdateStage.Failed"/>.</param>
public readonly record struct UpdateStatus(
    UpdateStage Stage = UpdateStage.NotChecked,
    string? Version = null,
    string? Message = null)
{
    /// <summary>Whether there is something a click would do right now.</summary>
    public bool CanInstall => Stage == UpdateStage.Available;

    /// <summary>Whether starting another check is valid right now.</summary>
    public bool CanCheck =>
        Stage is not UpdateStage.Checking and
        not UpdateStage.Installing and
        not UpdateStage.Restart;
}

/// <summary>
/// Finding out that a newer release exists, and installing it when asked.
/// <para>
/// The problem this solves is that until now nothing on a machine knew a release had
/// happened. Every fix in 0.09 through 0.12 was for something that stopped the agent
/// working, and every one of them reached a user only if that user happened to re-run
/// the install script — which is to say, only if they were already having the problem
/// the fix was for (issue #111).
/// </para>
/// <para>
/// Checking is automatic and installing is not. The click matters for one specific
/// reason: <b>wrappers do not reconnect</b>. A session whose agent goes away keeps
/// running at the desk — see <c>WrapperSession</c> — but is never shareable again, and
/// nothing tells the person holding the phone. So the agent will not restart itself
/// under live sessions at a moment of its own choosing, and when an update is installed
/// with sessions open it says so and waits rather than taking them out.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private readonly Func<CancellationToken, Task<string?>> _latestTag;
    private readonly Func<string, CancellationToken, Task<UpdateResult>> _install;
    private readonly Func<int> _liveSessions;
    private readonly Action _restart;
    private readonly UpdateOptions _options;
    private readonly string _currentVersion;
    private readonly Action<string>? _log;

    /// <summary>
    /// One update operation at a time. The periodic check and either button can arrive
    /// together; serializing checks and installs keeps their status transitions from
    /// overwriting one another and prevents two installers racing over the executable.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _statusLock = new();

    private UpdateStatus _status;

    public UpdateService(
        Func<CancellationToken, Task<string?>> latestTag,
        Func<string, CancellationToken, Task<UpdateResult>> install,
        Func<int> liveSessions,
        Action restart,
        UpdateOptions? options = null,
        string? currentVersion = null,
        Action<string>? log = null)
    {
        _latestTag = latestTag ?? throw new ArgumentNullException(nameof(latestTag));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _liveSessions = liveSessions ?? throw new ArgumentNullException(nameof(liveSessions));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _options = options ?? UpdateOptions.Default;
        _currentVersion = currentVersion ?? ProductVersion.Current;
        _log = log;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes, on whichever thread changed it.</summary>
    public event Action? Changed;

    public UpdateStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Checks now, and then every <see cref="UpdateOptions.Interval"/>, until cancelled.
    /// <para>
    /// Never throws. This runs beside the relay loop and the pipe server, and an agent
    /// that stopped relaying because a version check failed would be a far worse bug
    /// than the one this feature exists to fix.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Check)
        {
            _log?.Invoke("update: checking is turned off.");
            return;
        }

        try
        {
            await Task.Delay(_options.StartupDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_options.Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"update: the check loop stopped ({ex.Message}).");
        }
    }

    /// <summary>
    /// Asks which release is current and records the answer.
    /// <para>
    /// A failed check leaves <see cref="UpdateStage.Available"/> alone if it was already
    /// set: a machine that found 0.13 yesterday and is on a train today has not stopped
    /// having 0.13 to install, and replacing that with a network error would take away
    /// the one actionable thing the window had to say.
    /// </para>
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateStatus before = Status;

            if (before.Stage is UpdateStage.Installing or UpdateStage.Restart)
            {
                return before;
            }

            UpdateStatus baseline = before;

            if (before.Stage != UpdateStage.Checking)
            {
                Publish(new UpdateStatus(UpdateStage.Checking));
            }

            string? tag;

            try
            {
                tag = await _latestTag(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Publish(baseline);
                throw;
            }
            catch (Exception ex)
            {
                // A TaskCanceledException that is not this token is an HttpClient timeout,
                // which is an ordinary failed check and must not end the loop. Treating
                // every cancellation as shutdown would let one slow response stop a machine
                // ever checking again.
                _log?.Invoke($"update: could not reach github.com ({ex.Message}).");

                return Publish(baseline.Stage == UpdateStage.Available
                    ? baseline
                    : new UpdateStatus(UpdateStage.Failed, Message: $"Could not check for updates: {ex.Message}"));
            }

            if (!ReleaseVersion.IsUpgrade(tag, _currentVersion))
            {
                return Publish(new UpdateStatus(UpdateStage.UpToDate));
            }

            ReleaseVersion.TryParse(tag, out ReleaseVersion version);
            _log?.Invoke($"update: {version.Text} is available (this is {_currentVersion}).");

            return Publish(new UpdateStatus(UpdateStage.Available, version.Text));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Installs the release the last check found, and restarts the agent if it is safe
    /// to.
    /// <para>
    /// Safe means no sessions. The count is read after the install rather than before,
    /// because downloading and verifying takes long enough for someone to have started
    /// one in the meantime, and the whole point is not to take out a session that
    /// exists at the moment of the restart.
    /// </para>
    /// </summary>
    public async Task<UpdateStatus> InstallAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Status is not { Stage: UpdateStage.Available, Version: { Length: > 0 } version })
            {
                return Status;
            }

            Publish(new UpdateStatus(UpdateStage.Installing, version));

            UpdateResult result;

            try
            {
                result = await _install($"v{version}", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Publish(new UpdateStatus(UpdateStage.Available, version));
                throw;
            }
            catch (Exception ex)
            {
                return Publish(new UpdateStatus(UpdateStage.Failed, version, ex.Message));
            }

            if (!result.Ok)
            {
                _log?.Invoke($"update: {result.Message}");
                return Publish(new UpdateStatus(UpdateStage.Failed, version, result.Message));
            }

            _log?.Invoke($"update: {result.Message}");

            // Nothing was written, so the running process is already this build and
            // there is nothing to restart into.
            if (!result.Replaced)
            {
                return Publish(new UpdateStatus(UpdateStage.UpToDate));
            }

            int sessions = _liveSessions();

            if (sessions > 0)
            {
                // Deliberately not restarting. The message names the sessions because
                // otherwise this reads as the update having half-worked, and the user's
                // next move would be to go looking for the fault.
                return Publish(new UpdateStatus(
                    UpdateStage.Restart,
                    version,
                    sessions == 1
                        ? "Installed. It starts running when the session on this machine has finished."
                        : $"Installed. It starts running when the {sessions} sessions on this machine have finished."));
            }

            Publish(new UpdateStatus(UpdateStage.Restart, version));
            _restart();

            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a serialized check now, independently of the periodic timer.</summary>
    public void CheckSoon()
    {
        if (!_options.Check)
        {
            Publish(new UpdateStatus(
                UpdateStage.Failed,
                Message: "Update checks are disabled by configuration."));
            return;
        }

        if (Status.Stage is UpdateStage.Checking or UpdateStage.Installing or UpdateStage.Restart)
        {
            return;
        }

        _ = CheckRequestedAsync();
    }

    private async Task CheckRequestedAsync()
    {
        try
        {
            await CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"update: the requested check stopped ({ex.Message}).");
            Publish(new UpdateStatus(
                UpdateStage.Failed,
                Message: $"Could not check for updates: {ex.Message}"));
        }
    }

    private UpdateStatus Publish(UpdateStatus status)
    {
        bool changed;

        lock (_statusLock)
        {
            changed = _status != status;
            _status = status;
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return status;
    }
}
