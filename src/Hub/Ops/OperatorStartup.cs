using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// What the operator channel does at startup, and the one thing it keeps checking.
/// </summary>
public static class OperatorStartup
{
    /// <summary>
    /// Replays the persisted allowlist amendments and reports the start.
    /// <para>
    /// Called from <c>Program</c> before the app begins serving rather than from a hosted
    /// service, because the replay has to be complete before the first handshake is
    /// admitted. A <c>/deny</c> that took effect a few hundred milliseconds after the
    /// server started listening would be a revocation with a hole in it, and the hole
    /// would open at exactly the moment the process restarts — which is often the moment
    /// somebody is trying to get back in.
    /// </para>
    /// </summary>
    public static void Begin(IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);

        var store = services.GetRequiredService<OperatorStateStore>();
        var allowlist = services.GetRequiredService<AccountAllowlist>();
        var notifier = services.GetRequiredService<IOperatorNotifier>();
        TimeProvider time = services.GetRequiredService<TimeProvider>();

        Replay(store, allowlist, logger);

        DateTimeOffset now = time.GetUtcNow();

        (string? previous, int starts) = store.Mutate(state =>
        {
            string? was = state.LastVersion;

            state.LastVersion = ProductVersion.Current;
            state.WeekStarted ??= now;
            state.Starts++;

            return (was, state.Starts);
        });

        store.Flush();

        notifier.Send(new OperatorMessage.HubStarted(
            ProductVersion.Current,
            previous,
            allowlist.Count,
            starts));

        // Separate message, because it is a different kind of thing: the hub is up, and
        // it is refusing everybody. A total outage that looks like correct behaviour from
        // every angle except this one.
        if (allowlist.IsEmpty)
        {
            logger.LogWarning("The allowlist is empty, so every account will be refused.");
            notifier.Send(new OperatorMessage.AllowlistEmpty());
        }
    }

    private static void Replay(OperatorStateStore store, AccountAllowlist allowlist, ILogger logger)
    {
        (List<string> allowed, List<string> denied) = store.Read(state => (state.Allowed, state.Denied));

        foreach (string entry in allowed)
        {
            allowlist.Add(entry);
        }

        foreach (string entry in denied)
        {
            allowlist.Deny(entry);
        }

        if (allowed.Count == 0 && denied.Count == 0)
        {
            return;
        }

        // Said out loud, because a denial written down months ago overriding a config
        // entry added this morning is otherwise a genuinely baffling few hours.
        logger.LogInformation(
            "Applied {Allowed} allowlist addition(s) and {Denied} denial(s) from {Path}.",
            allowed.Count,
            denied.Count,
            store.Path);
    }
}

/// <summary>
/// Counts down to the Entra client secret expiring.
/// <para>
/// The failure this prevents is the nastiest kind: everything works, indefinitely, and
/// then one morning nobody can sign in and nothing changed. The hub never sees the
/// secret — it validates tokens against public signing keys — so the date is configured
/// rather than discovered, which is a fair trade for a value typed in once per renewal.
/// </para>
/// </summary>
public sealed class ClientSecretWatch(
    IOperatorNotifier notifier,
    IOptions<OperatorChannelOptions> options,
    TimeProvider time,
    ILogger<ClientSecretWatch> logger) : BackgroundService
{
    /// <summary>How often to look. A date does not move quickly.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>
    /// The days out at which to say something.
    /// <para>
    /// Thresholds rather than "every day under thirty", so the reminder stays a reminder.
    /// Thirty days is time to schedule it, seven is time to do it, and one is time to
    /// stop what you are doing.
    /// </para>
    /// </summary>
    private static readonly int[] Thresholds = [30, 14, 7, 3, 1, 0];

    private readonly OperatorChannelOptions _options = options.Value;
    private readonly HashSet<int> _reported = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ClientSecretExpiresOn is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Check();

                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "The client secret expiry check failed.");
            }
        }
    }

    private void Check()
    {
        if (_options.ClientSecretExpiresOn is not { } expires)
        {
            return;
        }

        int days = (int)Math.Floor((expires - time.GetUtcNow()).TotalDays);
        int? crossed = Thresholds.Where(threshold => days <= threshold).Max(threshold => (int?)threshold);

        if (crossed is null || !_reported.Add(crossed.Value))
        {
            return;
        }

        notifier.Send(new OperatorMessage.ClientSecretExpiring(Math.Max(days, 0)));
    }
}
