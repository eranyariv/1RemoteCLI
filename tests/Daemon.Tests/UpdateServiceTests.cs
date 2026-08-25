using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// What the agent does with what it finds.
/// <para>
/// The load-bearing rule is the last few tests: wrappers do not reconnect, so a session
/// whose agent goes away keeps running at the desk and is never shareable again, and
/// nothing tells the person holding the phone. Restarting under a live session would
/// turn a fix into an outage nobody is told about.
/// </para>
/// </summary>
public sealed class UpdateServiceTests
{
    private static readonly UpdateOptions Immediate = new()
    {
        StartupDelay = TimeSpan.Zero,
        Interval = TimeSpan.FromMilliseconds(10),
        RetryDelay = TimeSpan.FromMilliseconds(10),
        MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
    };

    private static UpdateService Service(
        Func<CancellationToken, Task<string?>>? latestTag = null,
        Func<string, CancellationToken, Task<UpdateResult>>? install = null,
        Func<int>? liveSessions = null,
        Action? restart = null,
        UpdateOptions? options = null,
        string current = "0.12",
        bool automaticUpdates = false) =>
        new(
            latestTag ?? (_ => Task.FromResult<string?>("v0.13")),
            install ?? ((_, _) => Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true))),
            liveSessions ?? (() => 0),
            restart ?? (() => { }),
            options: options ?? Immediate,
            currentVersion: current,
            automaticUpdatesEnabled: automaticUpdates);

    [Fact]
    public async Task SaysNothingBeforeItHasLookedAsync()
    {
        UpdateService updates = Service();

        Assert.Equal(UpdateStage.NotChecked, updates.Status.Stage);
        Assert.False(updates.Status.CanInstall);
        Assert.True(updates.Status.CanCheck);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task OffersANewerReleaseAsync()
    {
        UpdateService updates = Service();

        UpdateStatus status = await updates.CheckAsync();

        Assert.Equal(UpdateStage.Available, status.Stage);
        Assert.Equal("0.13", status.Version);
        Assert.True(status.CanInstall);
    }

    [Fact]
    public async Task OffersNothingWhenThisIsTheCurrentReleaseAsync()
    {
        UpdateStatus status = await Service(_ => Task.FromResult<string?>("v0.12")).CheckAsync();

        Assert.Equal(UpdateStage.UpToDate, status.Stage);
        Assert.False(status.CanInstall);
    }

    /// <summary>Anyone running a build from source is ahead of the tag.</summary>
    [Fact]
    public async Task OffersNothingWhenTheMachineIsAheadAsync()
    {
        UpdateStatus status = await Service(_ => Task.FromResult<string?>("v0.11")).CheckAsync();

        Assert.Equal(UpdateStage.UpToDate, status.Stage);
    }

    [Fact]
    public async Task OffersNothingWhenTheRepositoryHasNoReleasesAsync()
    {
        UpdateStatus status = await Service(_ => Task.FromResult<string?>(null)).CheckAsync();

        Assert.Equal(UpdateStage.UpToDate, status.Stage);
    }

    [Fact]
    public async Task ReportsACheckThatCouldNotReachGithubAsync()
    {
        UpdateStatus status = await Service(_ => throw new HttpRequestException("no route to host")).CheckAsync();

        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Contains("no route to host", status.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine that found 0.13 yesterday and is on a train today has not stopped
    /// having 0.13 to install, and replacing that with a network error would take away
    /// the one actionable thing the window had to say.
    /// </summary>
    [Fact]
    public async Task AFailedCheckDoesNotWithdrawAReleaseItAlreadyFoundAsync()
    {
        bool reachable = true;

        UpdateService updates = Service(_ => reachable
            ? Task.FromResult<string?>("v0.13")
            : throw new HttpRequestException("no route to host"));

        await updates.CheckAsync();
        reachable = false;

        UpdateStatus status = await updates.CheckAsync();

        Assert.Equal(UpdateStage.Available, status.Stage);
        Assert.Equal("0.13", status.Version);
        Assert.True(status.CanInstall);
    }

    [Fact]
    public async Task ARequestedFailedCheckDoesNotWithdrawAReleaseItAlreadyFoundAsync()
    {
        bool reachable = true;
        var failedCheck = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService updates = Service(_ => reachable
            ? Task.FromResult<string?>("v0.13")
            : failedCheck.Task);

        await updates.CheckAsync();
        reachable = false;
        var restored = new TaskCompletionSource<UpdateStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Changed += () =>
        {
            if (updates.Status.Stage == UpdateStage.Available)
            {
                restored.TrySetResult(updates.Status);
            }
        };
        updates.CheckSoon();

        Assert.Equal(UpdateStage.Checking, updates.Status.Stage);
        Assert.False(updates.Status.CanCheck);

        failedCheck.SetException(new HttpRequestException("no route to host"));
        UpdateStatus status = await restored.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(UpdateStage.Available, status.Stage);
        Assert.Equal("0.13", status.Version);
    }

    [Fact]
    public async Task UpdateClickWaitsForAnActiveCheckToConfirmTheReleaseAsync()
    {
        int checks = 0;
        var recheck = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool installed = false;
        UpdateService updates = Service(
            latestTag: _ => ++checks == 1
                ? Task.FromResult<string?>("v0.13")
                : recheck.Task,
            install: (_, _) =>
            {
                installed = true;
                return Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true));
            });

        await updates.CheckAsync();
        Task<UpdateStatus> checking = updates.CheckAsync();

        Assert.Equal(UpdateStage.Checking, updates.Status.Stage);

        Task<UpdateStatus> installing = updates.InstallAsync();
        recheck.SetResult("v0.13");
        await checking;
        UpdateStatus status = await installing;

        Assert.True(installed);
        Assert.Equal(UpdateStage.Restart, status.Stage);
    }

    /// <summary>
    /// An HttpClient timeout is a TaskCanceledException on a token that is not the
    /// caller's. Treating it as shutdown would let one slow response stop a machine ever
    /// checking again.
    /// </summary>
    [Fact]
    public async Task ATimeoutIsAFailedCheckAndNotAShutdownAsync()
    {
        UpdateStatus status = await Service(_ => throw new TaskCanceledException("timed out")).CheckAsync();

        Assert.Equal(UpdateStage.Failed, status.Stage);
    }

    [Fact]
    public async Task AnnouncesEveryChangeAsync()
    {
        UpdateService updates = Service();
        int changes = 0;
        updates.Changed += () => changes++;

        await updates.CheckAsync();

        // Checking, then Available.
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task SaysNothingWhenTheAnswerIsWhatItAlreadySaidAsync()
    {
        UpdateService updates = Service();
        await updates.CheckAsync();

        int changes = 0;
        updates.Changed += () => changes++;

        await updates.CheckAsync();

        // Checking and back to Available: the ends match, so the window is not
        // rewritten, but the transient is still announced.
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task InstallsNothingBeforeAnythingWasFoundAsync()
    {
        bool installed = false;

        UpdateService updates = Service(install: (_, _) =>
        {
            installed = true;
            return Task.FromResult(new UpdateResult(true, "Updated.", true));
        });

        await updates.InstallAsync();

        Assert.False(installed);
    }

    [Fact]
    public async Task InstallsTheReleaseItFoundAsync()
    {
        string? asked = null;

        UpdateService updates = Service(install: (tag, _) =>
        {
            asked = tag;
            return Task.FromResult(new UpdateResult(true, "Updated.", true));
        });

        await updates.CheckAsync();
        await updates.InstallAsync();

        Assert.Equal("v0.13", asked);
    }

    [Fact]
    public async Task RestartsWhenNothingIsRunningAsync()
    {
        bool restarted = false;
        UpdateService updates = Service(liveSessions: () => 0, restart: () => restarted = true);

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.True(restarted);
        Assert.Equal(UpdateStage.Restart, status.Stage);
    }

    /// <summary>
    /// The rule this whole design turns on. A restart here would leave a session running
    /// at the desk that the phone can never see again, and say nothing about it.
    /// </summary>
    [Fact]
    public async Task WillNotRestartUnderALiveSessionAsync()
    {
        bool restarted = false;
        UpdateService updates = Service(liveSessions: () => 1, restart: () => restarted = true);

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.False(restarted);
        Assert.Equal(UpdateStage.Restart, status.Stage);
        Assert.Contains("session", status.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NamesHowManySessionsAreHoldingItUpAsync()
    {
        UpdateService updates = Service(liveSessions: () => 3, restart: () => Assert.Fail("must not restart"));

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.Contains("3 sessions", status.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartsAfterTheFinalSessionEndsAsync()
    {
        int sessions = 1;
        int restarts = 0;
        UpdateService updates = Service(
            liveSessions: () => sessions,
            restart: () => Interlocked.Increment(ref restarts));

        await updates.CheckAsync();
        await updates.InstallAsync();

        sessions = 0;
        updates.ActivityChanged();
        updates.ActivityChanged();

        Assert.Equal(1, restarts);
    }

    /// <summary>
    /// Counted after the install, not before: downloading and verifying takes long
    /// enough for somebody to have started one in the meantime, and the point is not to
    /// take out a session that exists at the moment of the restart.
    /// </summary>
    [Fact]
    public async Task CountsSessionsAfterTheInstallRatherThanBeforeAsync()
    {
        int sessions = 0;
        bool restarted = false;

        UpdateService updates = Service(
            install: (_, _) =>
            {
                sessions = 1;
                return Task.FromResult(new UpdateResult(true, "Updated.", true));
            },
            liveSessions: () => sessions,
            restart: () => restarted = true);

        await updates.CheckAsync();
        await updates.InstallAsync();

        Assert.False(restarted);
    }

    /// <summary>
    /// Nothing was written, so the running process is already this build and there is
    /// nothing to restart into.
    /// </summary>
    [Fact]
    public async Task DoesNotRestartWhenNothingWasWrittenAsync()
    {
        bool restarted = false;

        UpdateService updates = Service(
            install: (_, _) => Task.FromResult(new UpdateResult(true, "Already running that build.", Replaced: false)),
            restart: () => restarted = true);

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.False(restarted);
        Assert.Equal(UpdateStage.UpToDate, status.Stage);
    }

    [Fact]
    public async Task ReportsAnInstallThatRefusedAsync()
    {
        bool restarted = false;

        UpdateService updates = Service(
            install: (_, _) => Task.FromResult(UpdateResult.Failure("the download does not match its checksum")),
            restart: () => restarted = true);

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.False(restarted);
        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Contains("checksum", status.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnInstallThatThrewAsync()
    {
        UpdateService updates = Service(install: (_, _) => throw new IOException("the disk is full"));

        await updates.CheckAsync();
        UpdateStatus status = await updates.InstallAsync();

        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Contains("the disk is full", status.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A check landing mid-install must not overwrite what the install is saying, and in
    /// particular must not put the window back to "install this" while it is installing.
    /// </summary>
    [Fact]
    public async Task ACheckDoesNotDisturbAnInstallAsync()
    {
        UpdateService updates = Service(liveSessions: () => 1);

        await updates.CheckAsync();
        await updates.InstallAsync();

        Assert.Equal(UpdateStage.Restart, updates.Status.Stage);

        UpdateStatus status = await updates.CheckAsync();

        Assert.Equal(UpdateStage.Restart, status.Stage);
    }

    [Fact]
    public async Task ChecksNothingWhenCheckingIsTurnedOffAsync()
    {
        bool asked = false;

        UpdateService updates = Service(
            latestTag: _ =>
            {
                asked = true;
                return Task.FromResult<string?>("v0.13");
            },
            options: Immediate with { Check = false });

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await updates.RunAsync(stopping.Token);

        Assert.False(asked);
    }

    [Fact]
    public async Task KeepsCheckingAsync()
    {
        int checks = 0;
        using var stopping = new CancellationTokenSource();

        UpdateService updates = Service(latestTag: _ =>
        {
            if (Interlocked.Increment(ref checks) >= 3)
            {
                stopping.Cancel();
            }

            return Task.FromResult<string?>("v0.12");
        });

        await updates.RunAsync(stopping.Token);

        Assert.True(checks >= 3, $"expected repeated checks, got {checks}");
    }

    [Fact]
    public async Task AutomaticallyInstallsAndRestartsAnIdleAgentAsync()
    {
        int installs = 0;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UpdateService updates = Service(
            install: (_, _) =>
            {
                Interlocked.Increment(ref installs);
                return Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true));
            },
            restart: stopping.Cancel,
            automaticUpdates: true);

        await updates.RunAsync(stopping.Token);

        Assert.Equal(1, installs);
        Assert.Equal(UpdateStage.Restart, updates.Status.Stage);
    }

    [Fact]
    public async Task AutomaticInstallWaitsForTheFinalSessionAsync()
    {
        int sessions = 1;
        int restarts = 0;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService updates = Service(
            liveSessions: () => sessions,
            restart: () =>
            {
                Interlocked.Increment(ref restarts);
                stopping.Cancel();
            },
            options: Immediate with { Interval = TimeSpan.FromHours(1) },
            automaticUpdates: true);
        updates.Changed += () =>
        {
            if (updates.Status.Stage == UpdateStage.Restart)
            {
                waiting.TrySetResult();
            }
        };

        Task running = updates.RunAsync(stopping.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, restarts);

        sessions = 0;
        updates.ActivityChanged();
        await running;

        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task TurningAutomaticUpdatesOffKeepsDiscoveryAndManualInstallAsync()
    {
        int installs = 0;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UpdateService updates = Service(
            latestTag: _ =>
            {
                stopping.Cancel();
                return Task.FromResult<string?>("v0.13");
            },
            install: (_, _) =>
            {
                Interlocked.Increment(ref installs);
                return Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true));
            },
            automaticUpdates: false);

        await updates.RunAsync(stopping.Token);

        Assert.Equal(0, installs);
        Assert.Equal(UpdateStage.Available, updates.Status.Stage);

        await updates.InstallAsync();
        Assert.Equal(1, installs);
    }

    [Fact]
    public async Task TurningAutomaticUpdatesOffStopsAnInFlightBackgroundCheckFromInstallingAsync()
    {
        int installs = 0;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var checkStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService updates = Service(
            latestTag: _ =>
            {
                checkStarted.TrySetResult();
                return response.Task;
            },
            install: (_, _) =>
            {
                Interlocked.Increment(ref installs);
                return Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true));
            },
            options: Immediate with { Interval = TimeSpan.FromHours(1) },
            automaticUpdates: true);
        updates.Changed += () =>
        {
            if (updates.Status.Stage == UpdateStage.Available)
            {
                stopping.Cancel();
            }
        };

        Task running = updates.RunAsync(stopping.Token);
        await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        updates.SetAutomaticUpdatesEnabled(false);
        response.SetResult("v0.13");
        await running;

        Assert.Equal(0, installs);
        Assert.Equal(UpdateStage.Available, updates.Status.Stage);
    }

    [Fact]
    public async Task EnablingAutomaticUpdatesWakesTheLoopAsync()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UpdateService updates = Service(
            restart: stopping.Cancel,
            options: Immediate with { StartupDelay = TimeSpan.FromHours(1) },
            automaticUpdates: false);

        Task running = updates.RunAsync(stopping.Token);
        updates.SetAutomaticUpdatesEnabled(true);
        await running;

        Assert.Equal(UpdateStage.Restart, updates.Status.Stage);
    }

    [Fact]
    public async Task AutomaticFailuresRetryWithBackoffAsync()
    {
        int checks = 0;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UpdateService updates = Service(
            latestTag: _ =>
            {
                int attempt = Interlocked.Increment(ref checks);
                return attempt < 3
                    ? throw new HttpRequestException("temporarily offline")
                    : Task.FromResult<string?>("v0.13");
            },
            restart: stopping.Cancel,
            automaticUpdates: true);

        await updates.RunAsync(stopping.Token);

        Assert.Equal(3, checks);
        Assert.Equal(UpdateStage.Restart, updates.Status.Stage);
    }

    /// <summary>
    /// An agent that stopped relaying because a version check threw would be a far worse
    /// bug than the one this feature exists to fix.
    /// </summary>
    [Fact]
    public async Task TheLoopSurvivesACheckThatFailsAsync()
    {
        int checks = 0;
        using var stopping = new CancellationTokenSource();

        UpdateService updates = Service(latestTag: _ =>
        {
            if (Interlocked.Increment(ref checks) >= 3)
            {
                stopping.Cancel();
            }

            throw new HttpRequestException("no route to host");
        });

        await updates.RunAsync(stopping.Token);

        Assert.True(checks >= 3, $"expected the loop to keep going, got {checks}");
    }

    [Fact]
    public async Task StopsWhenTheAgentDoesAsync()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        UpdateService updates = Service(options: new UpdateOptions { StartupDelay = TimeSpan.FromHours(1) });

        // Returns rather than throwing: it is awaited beside the relay loop.
        await updates.RunAsync(stopping.Token);
    }

    [Fact]
    public async Task AskingForACheckImmediatelyPublishesProgressAsync()
    {
        var response = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService updates = Service(_ => response.Task);
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Changed += () =>
        {
            if (updates.Status.Stage == UpdateStage.Available)
            {
                completed.TrySetResult();
            }
        };

        updates.CheckSoon();
        updates.CheckSoon();

        Assert.Equal(UpdateStage.Checking, updates.Status.Stage);
        Assert.False(updates.Status.CanCheck);

        response.SetResult("v0.13");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ARequestedCheckDoesNotAutomaticallyInstallAsync()
    {
        int installs = 0;
        var available = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService updates = Service(
            install: (_, _) =>
            {
                Interlocked.Increment(ref installs);
                return Task.FromResult(new UpdateResult(true, "Updated.", Replaced: true));
            },
            automaticUpdates: true);
        updates.Changed += () =>
        {
            if (updates.Status.Stage == UpdateStage.Available)
            {
                available.TrySetResult();
            }
        };

        updates.CheckSoon();
        await available.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, installs);
        Assert.Equal(UpdateStage.Available, updates.Status.Stage);
    }
}
