using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OneRemoteCli.Hub.Ops;

/// <summary>One account's totals for the current week.</summary>
public sealed class AccountTotals
{
    public int Sessions { get; set; }

    /// <summary>Bytes, not characters. See <c>OperatorMessage.Size</c> for why the distinction matters.</summary>
    public long Bytes { get; set; }

    public double DurationSeconds { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}

/// <summary>
/// Everything the operator channel has to remember for longer than a process lifetime.
/// <para>
/// Deliberately small, and deliberately all of it. A weekly digest is the first feature
/// in this product that genuinely needs memory: App Service restarts and cold starts are
/// frequent enough that "count in memory, flush on Sunday" would routinely report a
/// partial week as a whole one. <see cref="ObservedSeconds"/> and <see cref="Starts"/>
/// exist so the digest can state what it actually covers instead of implying complete.
/// </para>
/// <para>
/// Keyed by user key rather than username throughout, because usernames get reassigned
/// and the key is what everything else in the hub is keyed on. Usernames are carried
/// alongside in <see cref="Accounts"/> purely so a digest can name somebody.
/// </para>
/// </summary>
public sealed class OperatorState
{
    /// <summary>Schema version, so a future shape change can be migrated rather than guessed at.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>The hub version recorded at the last start. A different one means this start was a deploy.</summary>
    public string? LastVersion { get; set; }

    /// <summary>When the current counting window opened.</summary>
    public DateTimeOffset? WeekStarted { get; set; }

    /// <summary>When a digest was last sent, which is what keeps a restart from sending a second one.</summary>
    public DateTimeOffset? DigestSent { get; set; }

    /// <summary>How many times the process has started inside the current window.</summary>
    public int Starts { get; set; }

    /// <summary>How much of the window the hub was actually running for.</summary>
    public double ObservedSeconds { get; set; }

    /// <summary>User key to the username last seen on it. For naming accounts in a digest.</summary>
    public Dictionary<string, string> Accounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// User key to the first moment it ever connected.
    /// <para>
    /// The whole reason "a new user joined" needs storage. Without it, every restart
    /// re-announces everybody.
    /// </para>
    /// </summary>
    public Dictionary<string, DateTimeOffset> FirstSeen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-account totals for the current window.</summary>
    public Dictionary<string, AccountTotals> Week { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Accounts admitted by <c>/allow</c>, on top of the configured list.</summary>
    public List<string> Allowed { get; set; } = [];

    /// <summary>
    /// Accounts refused by <c>/deny</c>, overriding the configured list.
    /// <para>
    /// Persisted, and overriding, because the point of <c>/deny</c> is revoking a
    /// compromised account immediately. One that came back at the next restart because
    /// it is still in App Service configuration would be worse than not having the
    /// command: the operator would believe it was handled.
    /// </para>
    /// </summary>
    public List<string> Denied { get; set; } = [];

    /// <summary>
    /// The Bot API update cursor.
    /// <para>
    /// Persisted so a restart does not replay every command sent in the last 24 hours —
    /// which, for a channel that can change the allowlist, would be a genuinely bad
    /// afternoon.
    /// </para>
    /// </summary>
    public long UpdateOffset { get; set; }
}

/// <summary>
/// The state, on disk, in one JSON file.
/// <para>
/// <b>Why a file and not a database.</b> The rest of the hub is deliberately
/// stateless — the registry, the connection tokens and the push subscriptions are all in
/// memory, and a restart rebuilds them from clients reconnecting. This is the one thing
/// that cannot be rebuilt, and the options were a datastore the project then owns and
/// migrates, or telemetry it has to provision and query. App Service already gives every
/// app a persistent volume at <c>$HOME</c>, backed by Azure Files and shared by every
/// instance of the plan, which survives restarts and redeploys. A few kilobytes of JSON
/// on it needs no resource, no SDK, no schema and no migration, and it is inspectable
/// with a text editor when something looks wrong.
/// </para>
/// <para>
/// <b>What it assumes.</b> One instance. The plan is B1 with a single worker and the
/// registry already depends on that far more deeply than this does. Scale past one and
/// two processes would interleave writes to the same file — the digest would under-report
/// rather than corrupt, because writes are whole-file and atomic, but it would be wrong.
/// That is the same assumption <c>RelayRegistry</c> makes, recorded here so it is one
/// decision to revisit rather than two.
/// </para>
/// <para>
/// Written whole and moved into place, never updated where it lies. A process killed
/// mid-write — which App Service does routinely — leaves the previous file intact rather
/// than a truncated one that fails to parse on the next start.
/// </para>
/// </summary>
public sealed class OperatorStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly ILogger<OperatorStateStore> _logger;
    private readonly OperatorState _state;

    private bool _dirty;

    public OperatorStateStore(IOptions<OperatorChannelOptions> options, ILogger<OperatorStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        Path = ResolvePath(options.Value.StatePath);
        _state = Load();
    }

    /// <summary>Where the file lives. Exposed so a startup log can say where to look.</summary>
    public string Path { get; }

    /// <summary>
    /// Reads something out of the state under the lock.
    /// <para>
    /// A projection rather than a handle on the object: callers that held the state
    /// itself would read it while a mutation was half-applied, and the dictionaries here
    /// are not thread-safe by themselves.
    /// </para>
    /// </summary>
    public T Read<T>(Func<OperatorState, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        lock (_gate)
        {
            return read(_state);
        }
    }

    /// <summary>Changes the state under the lock and marks it for the next flush.</summary>
    public void Mutate(Action<OperatorState> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            change(_state);
            _dirty = true;
        }
    }

    /// <summary>
    /// Changes the state and reports something about the result in the same lock.
    /// <para>
    /// Exists because "add this if it is new, and tell me whether it was" is the shape of
    /// first-seen tracking, and splitting it into a read and a write would announce the
    /// same new user twice when two of their devices connect together.
    /// </para>
    /// </summary>
    public T Mutate<T>(Func<OperatorState, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            T result = change(_state);
            _dirty = true;

            return result;
        }
    }

    /// <summary>Writes the file if anything changed. Cheap and safe to call on a timer.</summary>
    public void Flush()
    {
        string json;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_state, Json);
            _dirty = false;
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a week of counters is a nuisance. Taking the hub down over it would
            // be a fault introduced entirely by a reporting feature, so it is logged and
            // the state stays in memory, where the next flush will try again.
            lock (_gate)
            {
                _dirty = true;
            }

            _logger.LogWarning(error, "Could not write the operator state file.");
        }
    }

    private OperatorState Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new OperatorState();
            }

            return JsonSerializer.Deserialize<OperatorState>(File.ReadAllText(Path), Json) ?? new OperatorState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Starting fresh beats refusing to start. The worst case is one wrong digest.
            _logger.LogWarning(error, "The operator state file could not be read; starting from empty.");
            return new OperatorState();
        }
    }

    /// <summary>
    /// Where the file goes when nobody has said.
    /// <para>
    /// <c>$HOME</c> is App Service's persistent share on both Linux and Windows, which is
    /// exactly the property this needs and the reason it is preferred over anything
    /// derived from the content root — that is wiped by every deploy. Off App Service the
    /// variable is either a real home directory or absent, and both fall through to
    /// somewhere sensible rather than failing.
    /// </para>
    /// </summary>
    internal static string ResolvePath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string? home = Environment.GetEnvironmentVariable("HOME");

        string root = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
            ? System.IO.Path.Combine(home, "data")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return System.IO.Path.Combine(root, "1RemoteCLI", "operator-state.json");
    }
}

/// <summary>
/// Folds the in-memory counters into the state and writes the file, periodically and
/// once more on the way down.
/// <para>
/// On a timer rather than on every change because the counters move on the hottest path
/// in the system — a file write per relayed frame would be absurd. Thirty seconds is the
/// most that can be lost to a hard kill, which for a weekly total is nothing.
/// </para>
/// </summary>
public sealed class OperatorStateFlusher(
    OperatorStateStore store,
    UsageCounters counters,
    TimeProvider time,
    ILogger<OperatorStateFlusher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);

                counters.Drain();
                store.Flush();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "The operator state flusher stopped.");
        }
        finally
        {
            // A graceful shutdown is the common case on App Service, and it is the one
            // chance to keep the last half-minute of the week.
            counters.Drain();
            store.Flush();
        }
    }
}
