namespace OneRemoteCli.Daemon.Chat;

/// <summary>
/// Retry and log-rate state for an unavailable local ACP executable.
/// </summary>
internal sealed class AcpDiscoveryBackoff
{
    internal static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    private TimeSpan _nextDelay = MinimumDelay;
    private string? _lastFailure;

    public int FailureCount { get; private set; }

    public AcpDiscoveryFailure Failed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        FailureCount = FailureCount == int.MaxValue ? int.MaxValue : FailureCount + 1;

        string fingerprint = $"{exception.GetType().FullName}:{exception.Message}";
        bool changed = !string.Equals(_lastFailure, fingerprint, StringComparison.Ordinal);
        _lastFailure = fingerprint;

        TimeSpan delay = _nextDelay;
        _nextDelay = TimeSpan.FromTicks(Math.Min(_nextDelay.Ticks * 2, MaximumDelay.Ticks));

        return new AcpDiscoveryFailure(
            FailureCount,
            delay,
            changed || IsPowerOfTwo(FailureCount));
    }

    /// <summary>Resets the retry sequence and returns the number of failures recovered from.</summary>
    public int Recovered()
    {
        int failures = FailureCount;
        FailureCount = 0;
        _nextDelay = MinimumDelay;
        _lastFailure = null;
        return failures;
    }

    private static bool IsPowerOfTwo(int value) =>
        value > 0 && (value & (value - 1)) == 0;
}

internal readonly record struct AcpDiscoveryFailure(
    int FailureCount,
    TimeSpan Delay,
    bool ShouldLog);
