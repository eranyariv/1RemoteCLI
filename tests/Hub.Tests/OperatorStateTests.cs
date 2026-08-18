using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The one thing in the hub that has to survive a restart, and the counters that feed it.
/// <para>
/// Everything else here is deliberately in memory and rebuilt by clients reconnecting.
/// This file exists because a weekly digest cannot be: App Service restarts often enough
/// that "count in memory, flush on Sunday" would report a fraction of a week as a whole
/// one, and would re-announce every user as new on every cold start.
/// </para>
/// </summary>
public class OperatorStateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhatWasWrittenComesBack()
    {
        Use(path =>
        {
            OperatorStateStore first = Open(path);

            first.Mutate(state =>
            {
                state.LastVersion = "1.2.3";
                state.UpdateOffset = 4321;
                state.Accounts["t:o"] = "someone@example.com";
                state.FirstSeen["t:o"] = Noon;
                state.Allowed.Add("t:allowed");
                state.Denied.Add("t:denied");
            });

            first.Flush();

            OperatorStateStore second = Open(path);

            Assert.Equal("1.2.3", second.Read(state => state.LastVersion));
            Assert.Equal(4321, second.Read(state => state.UpdateOffset));
            Assert.Equal("someone@example.com", second.Read(state => state.Accounts["t:o"]));
            Assert.Equal(Noon, second.Read(state => state.FirstSeen["t:o"]));
            Assert.Equal(["t:allowed"], second.Read(state => state.Allowed));
            Assert.Equal(["t:denied"], second.Read(state => state.Denied));
        });
    }

    /// <summary>
    /// Starting fresh beats refusing to start. The worst case is one wrong digest; the
    /// alternative is a hub that will not boot because of a reporting feature.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeParsedIsNotFatal()
    {
        Use(path =>
        {
            File.WriteAllText(path, "{ this is not json");

            OperatorStateStore store = Open(path);

            Assert.Empty(store.Read(state => state.Accounts));
            Assert.Equal(0, store.Read(state => state.UpdateOffset));
        });
    }

    [Fact]
    public void AMissingFileIsAnEmptyStateRatherThanAnError()
    {
        Use(path => Assert.Empty(Open(path).Read(state => state.FirstSeen)));
    }

    /// <summary>
    /// Written whole and moved into place. A process killed mid-write — which App Service
    /// does routinely — must leave the previous file intact rather than a truncated one.
    /// </summary>
    [Fact]
    public void NothingIsLeftBehindByAWrite()
    {
        Use(path =>
        {
            OperatorStateStore store = Open(path);

            store.Mutate(state => state.LastVersion = "1.0.0");
            store.Flush();

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    /// <summary>A flush with nothing to say does not rewrite the file.</summary>
    [Fact]
    public void FlushingAnUnchangedStateWritesNothing()
    {
        Use(path =>
        {
            OperatorStateStore store = Open(path);
            store.Flush();

            Assert.False(File.Exists(path));
        });
    }

    /// <summary>
    /// The whole reason first-seen needs storage: without it every restart re-announces
    /// everybody, and the operator learns to ignore the channel.
    /// </summary>
    [Fact]
    public void ANewUserIsAnnouncedOnceAndNotAgainAfterARestart()
    {
        Use(path =>
        {
            var time = new ManualTime(Noon);
            var notifier = new CollectingNotifier();

            OperatorStateStore store = Open(path);
            var counters = new UsageCounters(store, notifier, time);

            counters.AccountSeen("t:o", "someone@example.com");
            counters.AccountSeen("t:o", "someone@example.com");

            Assert.Single(notifier.Messages);
            Assert.IsType<OperatorMessage.AccountFirstSeen>(notifier.Messages[0]);

            store.Flush();

            // A restart: new store, new counters, same file.
            var afterRestart = new CollectingNotifier();
            new UsageCounters(Open(path), afterRestart, time).AccountSeen("t:o", "someone@example.com");

            Assert.Empty(afterRestart.Messages);
        });
    }

    /// <summary>A session that closes without ever having opened contributes nothing rather than a negative.</summary>
    [Fact]
    public void ASessionClosedWithoutAnOpenIsIgnored()
    {
        Use(path =>
        {
            var time = new ManualTime(Noon);
            OperatorStateStore store = Open(path);
            var counters = new UsageCounters(store, new CollectingNotifier(), time);

            counters.SessionClosed("t:o", "opened-before-this-process-started");
            counters.Drain();

            Assert.Empty(store.Read(state => state.Week));
        });
    }

    /// <summary>
    /// Observed time is what the counters actually watched, so the digest can say what it
    /// covers instead of implying a complete week.
    /// </summary>
    [Fact]
    public void DrainingAdvancesOnlyTheTimeThatWasActuallyWatched()
    {
        Use(path =>
        {
            var time = new ManualTime(Noon);
            OperatorStateStore store = Open(path);
            var counters = new UsageCounters(store, new CollectingNotifier(), time);

            time.Advance(TimeSpan.FromMinutes(10));
            counters.Drain();
            time.Advance(TimeSpan.FromMinutes(5));
            counters.Drain();

            Assert.Equal(TimeSpan.FromMinutes(15).TotalSeconds, store.Read(state => state.ObservedSeconds), 1);
        });
    }

    /// <summary>
    /// A handful of dead subscriptions overnight is normal and self-healing; a run of
    /// failures is an outage. Only the second is worth waking somebody for, and only once
    /// per window.
    /// </summary>
    [Fact]
    public void PushFailuresAreReportedOnceTheyStopLookingLikeNoise()
    {
        var notifier = new CollectingNotifier();
        var rates = new FailureRates(notifier, new ManualTime(Noon));

        for (int failure = 0; failure < FailureRates.Threshold - 1; failure++)
        {
            rates.PushFailed(expired: true);
        }

        Assert.Empty(notifier.Messages);

        rates.PushFailed(expired: false);
        Assert.Single(notifier.Messages);

        // Still failing is not news until the window rolls.
        for (int failure = 0; failure < 50; failure++)
        {
            rates.PushFailed(expired: false);
        }

        Assert.Single(notifier.Messages);

        var spike = Assert.IsType<OperatorMessage.PushFailuresSpiked>(notifier.Messages[0]);
        Assert.Equal(FailureRates.Threshold, spike.Failures);
        Assert.Equal(FailureRates.Threshold - 1, spike.Expired);
    }

    /// <summary>A quiet spell resets the count, so yesterday's failures never add to today's.</summary>
    [Fact]
    public void TheFailureWindowRolls()
    {
        var notifier = new CollectingNotifier();
        var time = new ManualTime(Noon);
        var rates = new FailureRates(notifier, time);

        for (int failure = 0; failure < FailureRates.Threshold; failure++)
        {
            rates.TokenRejected();
        }

        Assert.Single(notifier.Messages);

        time.Advance(FailureRates.Window + TimeSpan.FromMinutes(1));

        for (int failure = 0; failure < FailureRates.Threshold; failure++)
        {
            rates.TokenRejected();
        }

        Assert.Equal(2, notifier.Messages.Count);
        Assert.All(notifier.Messages, message => Assert.IsType<OperatorMessage.TokenFailuresSpiked>(message));
    }

    private static OperatorStateStore Open(string path) => new(
        Options.Create(new OperatorChannelOptions { StatePath = path }),
        NullLogger<OperatorStateStore>.Instance);

    private static void Use(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"operator-{Guid.NewGuid():N}.json");

        try
        {
            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
