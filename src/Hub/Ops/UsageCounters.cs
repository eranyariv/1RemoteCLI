using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OneRemoteCli.Protocol;

namespace OneRemoteCli.Hub.Ops;

/// <summary>
/// What the relay tells the operator channel.
/// <para>
/// <b>Every parameter here is a user key, a version, or a number.</b> No method takes a
/// <c>SessionAddress</c>, a <c>MachineInfo</c> or anything else carrying a display name,
/// which is what keeps a machine or session name from reaching the formatters by
/// accident — the mistake would be invisible at the call site, because the very same
/// field is correct to use for a push notification.
/// </para>
/// <para>
/// <c>sessionKey</c> is the one parameter that could carry an identifier, and it is
/// hashed on entry and never stored raw. It exists only so a close can be matched to the
/// open that started it; nothing derived from it is ever rendered.
/// </para>
/// </summary>
public interface IUsageRecorder
{
    /// <summary>An account connected. First time ever is the interesting case, and this finds it.</summary>
    void AccountSeen(string userKey, string? username);

    /// <summary>An agent registered a machine, running this version.</summary>
    void AgentSeen(string agentVersion);

    /// <summary>A session started.</summary>
    void SessionOpened(string userKey, string sessionKey);

    /// <summary>A session ended, closing the duration opened above.</summary>
    void SessionClosed(string userKey, string sessionKey);

    /// <summary>Terminal bytes moved through the relay. The hot path.</summary>
    void BytesRelayed(string userKey, int bytes);
}

/// <summary>Records nothing. What an unconfigured hub uses, so no call site needs a null check.</summary>
public sealed class NullUsageRecorder : IUsageRecorder
{
    public void AccountSeen(string userKey, string? username)
    {
    }

    public void AgentSeen(string agentVersion)
    {
    }

    public void SessionOpened(string userKey, string sessionKey)
    {
    }

    public void SessionClosed(string userKey, string sessionKey)
    {
    }

    public void BytesRelayed(string userKey, int bytes)
    {
    }
}

/// <summary>
/// The week's numbers, accumulated in memory and folded into the durable state on a
/// timer.
/// <para>
/// <b>Two tiers on purpose.</b> <see cref="BytesRelayed"/> is called once per relayed
/// frame — the hottest path in the system — so it may not take a lock or touch a file.
/// It adds to an interlocked counter and returns. Every thirty seconds
/// <see cref="Drain"/> moves the accumulated deltas into <see cref="OperatorStateStore"/>
/// under its lock, which is the only place the two meet.
/// </para>
/// <para>
/// The events that are rare and worth acting on immediately — a first-ever connection, a
/// version-skewed agent — go straight through, because a new user announced half a minute
/// late is fine but a new user announced only if the process survives long enough to
/// flush is not.
/// </para>
/// </summary>
public sealed class UsageCounters(
    OperatorStateStore store,
    IOperatorNotifier notifier,
    TimeProvider time) : IUsageRecorder
{
    private readonly OperatorStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IOperatorNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Correlation hash to when the session opened. Never the session id itself.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _open = new(StringComparer.Ordinal);

    /// <summary>Agent versions already reported as skewed, so one stale machine says it once.</summary>
    private readonly ConcurrentDictionary<string, byte> _skewed = new(StringComparer.Ordinal);

    private DateTimeOffset _drained = time.GetUtcNow();

    public void AccountSeen(string userKey, string? username)
    {
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return;
        }

        string account = string.IsNullOrWhiteSpace(username) ? string.Empty : username;
        DateTimeOffset now = _time.GetUtcNow();

        // Tested and recorded inside one lock. Two devices connecting together would
        // otherwise both see "not present" and announce the same new user twice.
        bool first = _store.Mutate(state =>
        {
            if (account.Length > 0)
            {
                state.Accounts[userKey] = account;
            }

            if (state.FirstSeen.ContainsKey(userKey))
            {
                return false;
            }

            state.FirstSeen[userKey] = now;
            return true;
        });

        if (first)
        {
            int ever = _store.Read(state => state.FirstSeen.Count);
            _notifier.Send(new OperatorMessage.AccountFirstSeen(account, ever));
        }
    }

    public void AgentSeen(string agentVersion)
    {
        if (string.IsNullOrWhiteSpace(agentVersion) ||
            string.Equals(agentVersion, ProductVersion.Current, StringComparison.Ordinal))
        {
            return;
        }

        // Once per version per process. A machine that reconnects on every tunnel change
        // would otherwise report the same skew all day.
        if (!_skewed.TryAdd(agentVersion, 0))
        {
            return;
        }

        _notifier.Send(new OperatorMessage.AgentVersionSkew(agentVersion, ProductVersion.Current, _skewed.Count));
    }

    public void SessionOpened(string userKey, string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(userKey) || string.IsNullOrWhiteSpace(sessionKey))
        {
            return;
        }

        Counters(userKey).Sessions.Add(1);
        _open[Correlate(userKey, sessionKey)] = _time.GetUtcNow();
    }

    public void SessionClosed(string userKey, string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(userKey) || string.IsNullOrWhiteSpace(sessionKey))
        {
            return;
        }

        if (!_open.TryRemove(Correlate(userKey, sessionKey), out DateTimeOffset started))
        {
            // Opened before this process started, or before the feature was configured.
            // Counting it as zero-length beats guessing, and beats counting it twice.
            return;
        }

        TimeSpan duration = _time.GetUtcNow() - started;

        if (duration > TimeSpan.Zero)
        {
            Counters(userKey).Duration.Add((long)duration.TotalMilliseconds);
        }
    }

    public void BytesRelayed(string userKey, int bytes)
    {
        if (bytes <= 0 || string.IsNullOrWhiteSpace(userKey))
        {
            return;
        }

        Counters(userKey).Bytes.Add(bytes);
    }

    /// <summary>
    /// Moves what has accumulated into the durable state, and advances the window's
    /// observed time.
    /// <para>
    /// Observed time is measured here rather than from the process start because that is
    /// what makes the digest's coverage line true across restarts: each drain adds only
    /// the interval it actually watched, so the total is time the hub was up, not time
    /// that elapsed.
    /// </para>
    /// </summary>
    public void Drain()
    {
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan since = now - _drained;
        _drained = now;

        // Each counter is taken to zero before it is added, so a frame arriving during
        // the drain lands in the next one rather than being counted twice or lost.
        var moved = new List<(string UserKey, long Sessions, long Bytes, long Milliseconds)>();

        foreach ((string userKey, Pending pending) in _pending)
        {
            long sessions = pending.Sessions.Take();
            long bytes = pending.Bytes.Take();
            long milliseconds = pending.Duration.Take();

            if (sessions != 0 || bytes != 0 || milliseconds != 0)
            {
                moved.Add((userKey, sessions, bytes, milliseconds));
            }
        }

        if (moved.Count == 0 && since <= TimeSpan.Zero)
        {
            return;
        }

        _store.Mutate(state =>
        {
            state.WeekStarted ??= now;

            if (since > TimeSpan.Zero)
            {
                state.ObservedSeconds += since.TotalSeconds;
            }

            foreach ((string userKey, long sessions, long bytes, long milliseconds) in moved)
            {
                if (!state.Week.TryGetValue(userKey, out AccountTotals? totals))
                {
                    totals = new AccountTotals();
                    state.Week[userKey] = totals;
                }

                totals.Sessions += (int)sessions;
                totals.Bytes += bytes;
                totals.DurationSeconds += milliseconds / 1000d;
            }
        });
    }

    private Pending Counters(string userKey) => _pending.GetOrAdd(userKey, _ => new Pending());

    /// <summary>
    /// Turns a session identifier into an opaque correlation token, immediately.
    /// <para>
    /// This is the boundary. A session id arrives, and what is kept is a hash of it — so
    /// even a future bug that serialised the whole of this class could not put a session
    /// id in a message. Salted with the user key so the same token cannot be recognised
    /// across accounts, and truncated because collisions here cost one session's duration.
    /// </para>
    /// </summary>
    private static string Correlate(string userKey, string sessionKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userKey}\u0000{sessionKey}")))[..16];

    /// <summary>Interlocked accumulators, so the relay path never blocks on the reporting path.</summary>
    private sealed class Pending
    {
        public Accumulator Sessions { get; } = new();

        public Accumulator Bytes { get; } = new();

        public Accumulator Duration { get; } = new();
    }

    private sealed class Accumulator
    {
        private long _value;

        public void Add(long amount) => Interlocked.Add(ref _value, amount);

        public long Take() => Interlocked.Exchange(ref _value, 0);
    }
}
