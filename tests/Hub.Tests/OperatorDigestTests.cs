using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// When the digest goes out, and what it says.
/// <para>
/// A weekly schedule is the hardest kind of code to be confident about, because getting
/// it wrong costs a week per attempt to observe. All of it is therefore pure — a function
/// of the clock and the last send — and all of the awkward cases are here: a hub that was
/// down over the slot, a restart minutes after a send, a hub started mid-week.
/// </para>
/// </summary>
public class OperatorDigestTests
{
    private static readonly DateTimeOffset MondayEight = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSlotIsTheMostRecentConfiguredMomentThatHasPassed()
    {
        Assert.Equal(
            MondayEight,
            DigestSchedule.MostRecentSlot(MondayEight.AddHours(3), DayOfWeek.Monday, 8));
    }

    /// <summary>
    /// The right weekday but before the hour: the slot that has passed is last week's,
    /// not one later today. Getting this backwards would send a digest every Monday
    /// morning at midnight.
    /// </summary>
    [Fact]
    public void OnTheRightDayButBeforeTheHourTheSlotIsLastWeeks()
    {
        Assert.Equal(
            MondayEight.AddDays(-7),
            DigestSchedule.MostRecentSlot(MondayEight.AddHours(-1), DayOfWeek.Monday, 8));
    }

    [Fact]
    public void MidweekTheSlotIsTheStartOfThisWeek()
    {
        Assert.Equal(
            MondayEight,
            DigestSchedule.MostRecentSlot(MondayEight.AddDays(3), DayOfWeek.Monday, 8));
    }

    /// <summary>
    /// Nothing sent yet means no digest, so a hub first started on a Wednesday does not
    /// immediately report a week it did not watch. The service records a baseline instead.
    /// </summary>
    [Fact]
    public void NothingIsDueBeforeABaselineExists()
    {
        Assert.False(DigestSchedule.Due(null, MondayEight.AddHours(1), DayOfWeek.Monday, 8));
    }

    [Fact]
    public void ADigestIsDueOnceTheSlotHasPassed()
    {
        Assert.True(DigestSchedule.Due(MondayEight.AddDays(-7), MondayEight, DayOfWeek.Monday, 8));
    }

    /// <summary>
    /// A restart minutes after a send must not produce a second digest — the slot it
    /// would answer has already been answered.
    /// </summary>
    [Fact]
    public void ARestartJustAfterASendDoesNotSendAgain()
    {
        Assert.False(DigestSchedule.Due(MondayEight, MondayEight.AddMinutes(5), DayOfWeek.Monday, 8));
    }

    /// <summary>
    /// The case that makes this "owed" rather than "it is now Monday": a hub down over
    /// the slot sends when it comes back, instead of skipping the week silently.
    /// </summary>
    [Fact]
    public void AHubThatWasDownOverTheSlotSendsWhenItReturns()
    {
        Assert.True(DigestSchedule.Due(
            MondayEight.AddDays(-7),
            MondayEight.AddHours(9),
            DayOfWeek.Monday,
            8));
    }

    [Fact]
    public void AnOutOfRangeHourIsClampedRatherThanThrowing()
    {
        Assert.Equal(23, DigestSchedule.MostRecentSlot(MondayEight.AddDays(1), DayOfWeek.Monday, 99).Hour);
        Assert.Equal(0, DigestSchedule.MostRecentSlot(MondayEight.AddDays(1), DayOfWeek.Monday, -5).Hour);
    }

    /// <summary>The digest reports what was counted, and names the accounts that arrived.</summary>
    [Fact]
    public void TheDigestAddsUpWhatWasCounted()
    {
        var time = new ManualTime(MondayEight);

        Use(time, (store, counters, options) =>
        {
            counters.AccountSeen("t:one", "one@example.com");
            counters.AccountSeen("t:two", "two@example.com");

            counters.SessionOpened("t:one", "s1");
            counters.BytesRelayed("t:one", 2048);
            time.Advance(TimeSpan.FromHours(2));
            counters.SessionClosed("t:one", "s1");

            counters.SessionOpened("t:two", "s2");
            counters.BytesRelayed("t:two", 1024);
            time.Advance(TimeSpan.FromMinutes(30));
            counters.SessionClosed("t:two", "s2");

            counters.Drain();

            OperatorMessage.WeeklyDigest digest =
                store.Read(state => DigestBuilder.Build(state, options, time.GetUtcNow()));

            Assert.Equal(2, digest.Sessions);
            Assert.Equal(3072, digest.Bytes);
            Assert.Equal(TimeSpan.FromHours(2.5), digest.Duration);
            Assert.Equal(2, digest.ActiveAccounts);
            Assert.Equal(["one@example.com", "two@example.com"], digest.NewAccounts);

            // Ordered by session time, so the busiest account is first rather than
            // whichever the dictionary happened to yield.
            Assert.Equal("one@example.com", digest.TopAccounts[0].Account);
        });
    }

    /// <summary>
    /// The cost is prorated from a configured monthly charge, because the hub has no
    /// credential for the Cost Management API and should not be given one.
    /// </summary>
    [Fact]
    public void TheCostIsProratedFromTheConfiguredMonthlyCharge()
    {
        var time = new ManualTime(MondayEight);

        Use(time, (store, counters, options) =>
        {
            store.Mutate(state => state.WeekStarted = MondayEight.AddDays(-7));

            decimal cost = store.Read(state => DigestBuilder.Build(state, options, time.GetUtcNow())).Cost;

            // 13/month * 12 months / 365 days * 7 days.
            Assert.Equal(Math.Round(13m * 12m / 365m * 7m, 2), cost);
        });
    }

    /// <summary>A window nobody paid for reports no cost rather than "$0.00 for the week".</summary>
    [Fact]
    public void AnUnconfiguredCostIsNotReported()
    {
        var time = new ManualTime(MondayEight);
        var options = new OperatorChannelOptions { MonthlyCost = 0 };

        Use(time, options, (store, counters, _) =>
        {
            string rendered = store.Read(state => DigestBuilder.Build(state, options, time.GetUtcNow())).Render();

            Assert.DoesNotContain("for the week", rendered, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Closing the window clears the week but keeps what spans weeks — who has ever been
    /// seen, and what they are called — or every digest would announce everybody as new.
    /// </summary>
    [Fact]
    public void ClosingTheWindowKeepsWhatSpansWeeks()
    {
        var time = new ManualTime(MondayEight);

        Use(time, (store, counters, options) =>
        {
            counters.AccountSeen("t:one", "one@example.com");
            counters.SessionOpened("t:one", "s1");
            counters.Drain();

            store.Mutate(state => DigestBuilder.Reset(state, time.GetUtcNow()));

            Assert.Empty(store.Read(state => state.Week));
            Assert.Equal(0, store.Read(state => state.ObservedSeconds));
            Assert.Single(store.Read(state => state.FirstSeen));
            Assert.Equal("one@example.com", store.Read(state => state.Accounts["t:one"]));

            // And the next digest reports nobody as new, because they were seen last week.
            Assert.Empty(store.Read(state => DigestBuilder.Build(state, options, time.GetUtcNow())).NewAccounts);
        });
    }

    private static void Use(ManualTime time, Action<OperatorStateStore, UsageCounters, OperatorChannelOptions> test) =>
        Use(time, new OperatorChannelOptions { MonthlyCost = 13m }, test);

    private static void Use(
        ManualTime time,
        OperatorChannelOptions options,
        Action<OperatorStateStore, UsageCounters, OperatorChannelOptions> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"operator-{Guid.NewGuid():N}.json");
        options.StatePath = path;

        try
        {
            var store = new OperatorStateStore(Options.Create(options), NullLogger<OperatorStateStore>.Instance);

            store.Mutate(state => state.WeekStarted ??= time.GetUtcNow());
            test(store, new UsageCounters(store, new CollectingNotifier(), time), options);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
