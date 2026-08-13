using OneRemoteCli.Hub.Auth;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The rules that keep a live socket from outliving its token.
/// <para>
/// Worth stating plainly, because it is the thing every SignalR design gets wrong:
/// the token is checked at the handshake and never again. Without what these tests
/// cover, revoking somebody's access has no effect on the connection they already
/// have, for as long as they keep it open.
/// </para>
/// </summary>
public sealed class ConnectionTokensTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
    [Fact]
    public void AConnectionIsAskedToRefreshBeforeItsTokenRunsOut()
    {
        ConnectionTokens tokens = new(_time);
        tokens.Track("a", "user", _time.GetUtcNow().AddMinutes(10), () => { });

        Assert.Empty(tokens.Sweep());

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(["a"], tokens.Sweep());
    }

    /// <summary>
    /// Asking once is the point. A warning repeated every sweep would be thirty
    /// pointless round trips over the five minutes before expiry, on exactly the
    /// connections that are already struggling.
    /// </summary>
    [Fact]
    public void ItIsAskedOnlyOnce()
    {
        ConnectionTokens tokens = new(_time);
        tokens.Track("a", "user", _time.GetUtcNow().AddMinutes(1), () => { });

        Assert.Equal(["a"], tokens.Sweep());
        Assert.Empty(tokens.Sweep());
    }

    [Fact]
    public void ARefreshedTokenEarnsAFreshWarningLater()
    {
        ConnectionTokens tokens = new(_time);
        tokens.Track("a", "user", _time.GetUtcNow().AddMinutes(1), () => { });

        Assert.Equal(["a"], tokens.Sweep());

        tokens.Renew("a", _time.GetUtcNow().AddHours(1));
        Assert.Empty(tokens.Sweep());

        _time.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal(["a"], tokens.Sweep());
    }

    [Fact]
    public void AConnectionThatNeverRefreshesIsDropped()
    {
        ConnectionTokens tokens = new(_time);
        bool aborted = false;

        tokens.Track("a", "user", _time.GetUtcNow().AddMinutes(1), () => aborted = true);

        _time.Advance(TimeSpan.FromMinutes(1));
        tokens.Sweep();
        Assert.False(aborted);

        // Past expiry, but still inside the grace the handshake itself allows.
        _time.Advance(ConnectionTokens.Grace);
        tokens.Sweep();

        Assert.True(aborted);
        Assert.Equal(0, tokens.Count);
    }

    /// <summary>
    /// The abort raises a disconnect, which calls <c>Forget</c>. If the sweeper had
    /// not already removed the entry, the pass after would abort a connection that no
    /// longer exists — harmless here, but the same shape of bug is what produces
    /// double-disconnect storms.
    /// </summary>
    [Fact]
    public void ItIsDroppedOnlyOnce()
    {
        ConnectionTokens tokens = new(_time);
        int aborts = 0;

        tokens.Track("a", "user", _time.GetUtcNow(), () => aborts++);

        _time.Advance(ConnectionTokens.Grace + TimeSpan.FromSeconds(1));
        tokens.Sweep();
        tokens.Sweep();

        Assert.Equal(1, aborts);
    }

    [Fact]
    public void ARefreshInTimeSavesTheConnection()
    {
        ConnectionTokens tokens = new(_time);
        bool aborted = false;

        tokens.Track("a", "user", _time.GetUtcNow().AddMinutes(1), () => aborted = true);
        tokens.Renew("a", _time.GetUtcNow().AddHours(1));

        _time.Advance(TimeSpan.FromMinutes(30));
        tokens.Sweep();

        Assert.False(aborted);
        Assert.Equal(1, tokens.Count);
    }

    /// <summary>
    /// A connection the sweeper has already given up on must not be able to reinstate
    /// itself by answering a warning late.
    /// </summary>
    [Fact]
    public void ARefreshForAConnectionThatIsAlreadyGoneDoesNothing()
    {
        ConnectionTokens tokens = new(_time);
        tokens.Renew("never-tracked", _time.GetUtcNow().AddHours(1));

        Assert.Equal(0, tokens.Count);
        Assert.Null(tokens.ExpiryOf("never-tracked"));
    }

    [Fact]
    public void AForgottenConnectionIsNotSwept()
    {
        ConnectionTokens tokens = new(_time);
        bool aborted = false;

        tokens.Track("a", "user", _time.GetUtcNow(), () => aborted = true);
        tokens.Forget("a");

        _time.Advance(TimeSpan.FromHours(1));
        tokens.Sweep();

        Assert.False(aborted);
    }

    /// <summary>
    /// Admission has already decided the token is genuine. Disconnecting somebody over
    /// a claim the hub could not parse would turn our misunderstanding into their
    /// outage, which is the wrong way round.
    /// </summary>
    [Fact]
    public void ATokenWithNoReadableExpiryIsLeftAlone()
    {
        ConnectionTokens tokens = new(_time);
        bool aborted = false;

        tokens.Track("a", "user", null, () => aborted = true);

        _time.Advance(TimeSpan.FromDays(30));
        tokens.Sweep();

        Assert.False(aborted);
        Assert.Equal(0, tokens.Count);
    }

    [Fact]
    public void TheConnectionRemembersWhoItWasAdmittedAs()
    {
        ConnectionTokens tokens = new(_time);
        tokens.Track("a", "tenant:object", _time.GetUtcNow().AddHours(1), () => { });

        Assert.Equal("tenant:object", tokens.UserKeyOf("a"));
        Assert.Null(tokens.UserKeyOf("b"));
    }

    /// <summary>One slow connection's expiry must not delay another's warning.</summary>
    [Fact]
    public void EachConnectionIsJudgedOnItsOwnToken()
    {
        ConnectionTokens tokens = new(_time);
        bool soonAborted = false;

        tokens.Track("soon", "user", _time.GetUtcNow().AddSeconds(1), () => soonAborted = true);
        tokens.Track("later", "user", _time.GetUtcNow().AddHours(2), () => { });

        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(tokens.Sweep());
        Assert.True(soonAborted);
        Assert.Equal(1, tokens.Count);
        Assert.NotNull(tokens.ExpiryOf("later"));
    }
}

/// <summary>A clock the test moves, so expiry can be reached without waiting for it.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
