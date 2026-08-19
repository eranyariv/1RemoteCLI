using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.13")]
    [InlineData("v0.13")]
    [InlineData("V0.13")]
    [InlineData(" 0.13 ")]
    [InlineData("0.13+abc123")]
    public void ReadsTheFormsAReleaseIsWrittenIn(string value)
    {
        Assert.True(ReleaseVersion.TryParse(value, out ReleaseVersion version));
        Assert.Equal("0.13", version.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("0")]
    [InlineData("0.x")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.2")]
    public void RefusesAnythingThatIsNotOne(string? value) =>
        Assert.False(ReleaseVersion.TryParse(value, out _));

    /// <summary>
    /// The bug this type exists to prevent. As strings "0.9" sorts after "0.10", so a
    /// machine on 0.9 would consider itself ahead of every release for ninety of them.
    /// </summary>
    [Fact]
    public void ComparesTheMinorPartAsANumber()
    {
        Assert.True(ReleaseVersion.IsUpgrade("v0.10", "0.9"));
        Assert.False(ReleaseVersion.IsUpgrade("v0.9", "0.10"));
    }

    [Fact]
    public void TheSameReleaseIsNotAnUpgrade() => Assert.False(ReleaseVersion.IsUpgrade("v0.12", "0.12"));

    /// <summary>
    /// Anyone running a build from source is ahead of the tag, and must not be moved
    /// backwards by a check that found the last release.
    /// </summary>
    [Fact]
    public void AnOlderReleaseIsNotAnUpgrade() => Assert.False(ReleaseVersion.IsUpgrade("v0.11", "0.12"));

    /// <summary>
    /// "Could not read it" must never mean "probably newer": that would have the agent
    /// install whatever a mangled tag pointed at.
    /// </summary>
    [Theory]
    [InlineData("latest", "0.12")]
    [InlineData(null, "0.12")]
    [InlineData("v0.13", "not-a-version")]
    public void AnUnreadableVersionIsNeverAnUpgrade(string? candidate, string? current) =>
        Assert.False(ReleaseVersion.IsUpgrade(candidate, current));

    [Fact]
    public void AMissingThirdPartIsZero()
    {
        Assert.True(ReleaseVersion.TryParse("0.13", out ReleaseVersion two));
        Assert.True(ReleaseVersion.TryParse("0.13.0", out ReleaseVersion three));

        Assert.Equal(0, two.CompareTo(three));
        Assert.False(ReleaseVersion.IsUpgrade("0.13.0", "0.13"));
    }

    [Fact]
    public void APatchIsAnUpgrade() => Assert.True(ReleaseVersion.IsUpgrade("v0.13.1", "0.13"));

    [Fact]
    public void OrdersARunOfReleases()
    {
        string[] releases = ["0.01", "0.09", "0.10", "0.12", "1.00"];

        for (int i = 1; i < releases.Length; i++)
        {
            Assert.True(
                ReleaseVersion.IsUpgrade(releases[i], releases[i - 1]),
                $"{releases[i]} should be newer than {releases[i - 1]}");
        }
    }
}
