using OneRemoteCli.Daemon.Chat;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AcpDiscoveryBackoffTests
{
    [Fact]
    public void ConsecutiveFailuresBackOffToFiveMinutes()
    {
        var backoff = new AcpDiscoveryBackoff();
        TimeSpan[] delays =
        [
            .. Enumerable.Range(0, 9)
                .Select(_ => backoff.Failed(new InvalidOperationException("missing")).Delay),
        ];

        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(80),
                TimeSpan.FromSeconds(160),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5),
            ],
            delays);
    }

    [Fact]
    public void IdenticalFailuresLogOnlyRateLimitedSummaries()
    {
        var backoff = new AcpDiscoveryBackoff();
        bool[] decisions =
        [
            .. Enumerable.Range(0, 9)
                .Select(_ => backoff.Failed(new InvalidOperationException("missing")).ShouldLog),
        ];

        Assert.Equal([true, true, false, true, false, false, false, true, false], decisions);
    }

    [Fact]
    public void AChangedFailureIsLoggedImmediately()
    {
        var backoff = new AcpDiscoveryBackoff();

        backoff.Failed(new InvalidOperationException("missing"));
        backoff.Failed(new InvalidOperationException("missing"));
        backoff.Failed(new InvalidOperationException("missing"));
        AcpDiscoveryFailure changed = backoff.Failed(new InvalidOperationException("login failed"));

        Assert.True(changed.ShouldLog);
        Assert.Equal(4, changed.FailureCount);
    }

    [Fact]
    public void RecoveryResetsTheSequenceForAutomaticRediscovery()
    {
        var backoff = new AcpDiscoveryBackoff();
        backoff.Failed(new InvalidOperationException("missing"));
        backoff.Failed(new InvalidOperationException("missing"));
        backoff.Failed(new InvalidOperationException("missing"));

        Assert.Equal(3, backoff.Recovered());

        AcpDiscoveryFailure next = backoff.Failed(new InvalidOperationException("missing"));
        Assert.Equal(1, next.FailureCount);
        Assert.Equal(TimeSpan.FromSeconds(5), next.Delay);
        Assert.True(next.ShouldLog);
    }
}
