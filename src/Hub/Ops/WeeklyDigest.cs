using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// When the digest is due.
/// <para>
/// A pure function of the clock and the last send, kept out of the
/// <see cref="WeeklyDigestService"/> so every interesting case — a hub that was down over
/// the slot, a restart an hour after one was sent, a clock that jumps — is a unit test
/// rather than something you wait a week to observe. Same shape, and same reason, as
/// <c>ConnectionTokens.Sweep</c> sitting outside <c>TokenExpirySweeper</c>.
/// </para>
/// </summary>
public static class DigestSchedule
{
    /// <summary>
    /// The most recent moment the digest should have gone out, at or before
    /// <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset MostRecentSlot(DateTimeOffset now, DayOfWeek day, int hourUtc)
    {
        int hour = Math.Clamp(hourUtc, 0, 23);

        var slot = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(hour);
        slot = slot.AddDays(-(((int)slot.DayOfWeek - (int)day + 7) % 7));

        // Today is the right weekday but the hour has not come round yet.
        return slot > now ? slot.AddDays(-7) : slot;
    }

    /// <summary>
    /// Whether a digest is owed.
    /// <para>
    /// Owed means "a slot has passed that was not covered by the last send", which is
    /// what makes a hub that was down over Monday morning send on Monday afternoon
    /// instead of skipping the week. It also means a restart five minutes after a digest
    /// does not send a second one, because the slot it would answer has already been
    /// answered.
    /// </para>
    /// <para>
    /// Never due when nothing has been sent yet: the first run records a baseline
    /// instead, so a hub started on a Tuesday does not immediately report a week it did
    /// not watch.
    /// </para>
    /// </summary>
    public static bool Due(DateTimeOffset? lastSent, DateTimeOffset now, DayOfWeek day, int hourUtc) =>
        lastSent is not null && MostRecentSlot(now, day, hourUtc) > lastSent.Value;
}

/// <summary>
/// Turns the accumulated state into the week's message, and closes the window.
/// <para>
/// Separated from both the store and the timer so the numbers can be asserted directly.
/// </para>
/// </summary>
public static class DigestBuilder
{
    /// <summary>How many accounts a digest names. A digest is read on a phone.</summary>
    private const int Top = 5;

    /// <summary>Reads the window out of the state. Does not change it.</summary>
    public static OperatorMessage.WeeklyDigest Build(
        OperatorState state,
        OperatorChannelOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        DateTimeOffset from = state.WeekStarted ?? now - TimeSpan.FromDays(7);
        TimeSpan window = now - from;

        // Half-open: [from, now). An account first seen at the exact instant a digest is
        // built belongs to the week that is starting, not the one being closed — and
        // since Reset sets the next WeekStarted to this same instant, an inclusive upper
        // bound would announce it as new in both digests.
        List<string> arrived = [.. state.FirstSeen
            .Where(entry => entry.Value >= from && entry.Value < now)
            .OrderBy(entry => entry.Value)
            .Select(entry => Name(state, entry.Key))];

        List<AccountActivity> busiest = [.. state.Week
            .Select(entry => new AccountActivity(
                Name(state, entry.Key),
                entry.Value.Sessions,
                entry.Value.Bytes,
                entry.Value.Duration))
            .OrderByDescending(activity => activity.Duration)
            .ThenByDescending(activity => activity.Bytes)
            .Take(Top)];

        return new OperatorMessage.WeeklyDigest(
            From: from,
            To: now,
            Sessions: state.Week.Values.Sum(totals => totals.Sessions),
            Bytes: state.Week.Values.Sum(totals => totals.Bytes),
            Duration: TimeSpan.FromSeconds(state.Week.Values.Sum(totals => totals.DurationSeconds)),
            ActiveAccounts: state.Week.Count,
            NewAccounts: arrived,
            TopAccounts: busiest,
            Cost: Cost(options.MonthlyCost, window),
            Currency: options.Currency,
            Observed: TimeSpan.FromSeconds(Math.Min(state.ObservedSeconds, window.TotalSeconds)),
            Restarts: Math.Max(state.Starts, 1));
    }

    /// <summary>Opens a fresh window. The counters that carry across weeks — first-seen, the account names — stay.</summary>
    public static void Reset(OperatorState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.Week.Clear();
        state.WeekStarted = now;
        state.ObservedSeconds = 0;
        state.Starts = 1;
        state.DigestSent = now;
    }

    /// <summary>
    /// The configured monthly charge, prorated to the window actually reported.
    /// <para>
    /// Twelve months over 365 days rather than dividing by 30, so a year of weekly
    /// digests adds up to a year of the plan instead of about six days more.
    /// </para>
    /// </summary>
    private static decimal Cost(decimal monthly, TimeSpan window) => monthly <= 0
        ? 0
        : Math.Round(monthly * 12m / 365m * (decimal)Math.Max(window.TotalDays, 0), 2);

    /// <summary>
    /// The username for an account, or a placeholder.
    /// <para>
    /// Never the user key. It would be an identifier the operator can act on, but a
    /// digest full of guid pairs is unreadable, and the account is only in the digest at
    /// all because it has been seen — which means a username was recorded with it.
    /// </para>
    /// </summary>
    private static string Name(OperatorState state, string userKey) =>
        state.Accounts.TryGetValue(userKey, out string? username) && !string.IsNullOrWhiteSpace(username)
            ? username
            : string.Empty;
}

/// <summary>
/// Sends the digest when it is due.
/// <para>
/// Deliberately thin, in the shape of <c>TokenExpirySweeper</c>: everything worth testing
/// is in <see cref="DigestSchedule"/> and <see cref="DigestBuilder"/>, so the interesting
/// cases are covered without waiting on a real timer — or on a real week.
/// </para>
/// </summary>
public sealed class WeeklyDigestService(
    OperatorStateStore store,
    UsageCounters counters,
    IOperatorNotifier notifier,
    IOptions<OperatorChannelOptions> options,
    TimeProvider time,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    /// <summary>
    /// How often to check.
    /// <para>
    /// Fifteen minutes against a weekly cadence: the digest arrives within a quarter of
    /// an hour of its slot, and the check is a comparison of two timestamps.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly OperatorChannelOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The baseline, so a hub first started mid-week reports at the next slot rather
        // than immediately reporting a week it did not watch.
        store.Mutate(state =>
        {
            state.DigestSent ??= time.GetUtcNow();
            state.WeekStarted ??= time.GetUtcNow();
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);

                if (Due())
                {
                    Send();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // A digest that throws must not take the service with it, or the failure
                // silently becomes permanent.
                logger.LogError(error, "The weekly digest failed.");
            }
        }
    }

    /// <summary>Builds and sends the digest now, closing the window. Also what <c>/digest</c> calls.</summary>
    public void Send()
    {
        // Drained first, or the digest reports everything except the last half-minute —
        // and on a manual /digest, possibly everything except the whole session that
        // prompted it.
        counters.Drain();

        DateTimeOffset now = time.GetUtcNow();

        OperatorMessage.WeeklyDigest digest = store.Mutate(state =>
        {
            OperatorMessage.WeeklyDigest built = DigestBuilder.Build(state, _options, now);
            DigestBuilder.Reset(state, now);

            return built;
        });

        notifier.Send(digest);
        store.Flush();
    }

    private bool Due() => DigestSchedule.Due(
        store.Read(state => state.DigestSent),
        time.GetUtcNow(),
        _options.DigestDay,
        _options.DigestHourUtc);
}
