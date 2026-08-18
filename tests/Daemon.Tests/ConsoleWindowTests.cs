using OneRemoteCli.Daemon.Cli;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Deciding whether the console belongs to the agent alone.
/// <para>
/// The hiding itself needs a real console and a real window, so what is tested here
/// is the judgement that guards it. Getting this wrong in the permissive direction
/// hides the terminal a user is sitting in front of, which is a far worse bug than
/// the one being fixed, so the safe answers are asserted explicitly.
/// </para>
/// </summary>
public sealed class ConsoleWindowTests
{
    [Fact]
    public void AConsoleWithOnlyThisProcessOnItWasMadeForThisProcess()
    {
        Assert.True(ConsoleWindow.IsOursAlone(1));
    }

    [Fact]
    public void AConsoleSharedWithAShellIsLeftAlone()
    {
        Assert.False(ConsoleWindow.IsOursAlone(2));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    public void SoIsOneSharedWithSeveral(int attached)
    {
        Assert.False(ConsoleWindow.IsOursAlone(attached));
    }

    /// <summary>
    /// Zero is what the API returns when it fails, not a console with nothing on it,
    /// and a failed question is not permission to hide anything.
    /// </summary>
    [Fact]
    public void AFailedCountHidesNothing()
    {
        Assert.False(ConsoleWindow.IsOursAlone(0));
    }
}
