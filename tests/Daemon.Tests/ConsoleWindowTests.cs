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

    /// <summary>
    /// The fault this was written for, and the one that made it look intermittent: the
    /// console window does not exist the instant the process does. A look that came
    /// too early has to be repeated, not treated as "no console".
    /// </summary>
    [Fact]
    public void AWindowThatHasNotAppearedYetIsWaitedFor()
    {
        int looks = 0;
        bool hidden = false;

        ConsoleWindow.Verdict verdict = ConsoleWindow.HideWhenReady(
            hasWindow: () => ++looks >= 4,
            attachedProcesses: () => 1,
            hide: () => hidden = true,
            pause: () => { },
            attempts: 25);

        Assert.Equal(ConsoleWindow.Verdict.Hide, verdict);
        Assert.True(hidden);
        Assert.Equal(4, looks);
    }

    /// <summary>
    /// A terminal the user is sitting in front of. Judged on the first look and never
    /// reconsidered, so nothing that happens later in the agent's life can decide to
    /// hide the window somebody is typing into.
    /// </summary>
    [Fact]
    public void AConsoleAlreadySharedIsAnsweredOnceAndLeftAlone()
    {
        int looks = 0;

        ConsoleWindow.Verdict verdict = ConsoleWindow.HideWhenReady(
            hasWindow: () => true,
            attachedProcesses: () =>
            {
                looks++;

                return 2;
            },
            hide: () => Assert.Fail("Hid a console a shell was sharing."),
            pause: () => Assert.Fail("Waited to see whether a shell would go away."),
            attempts: 25);

        Assert.Equal(ConsoleWindow.Verdict.LeaveAlone, verdict);
        Assert.Equal(1, looks);
    }

    /// <summary>
    /// No console at all — the agent started detached, or in a service-like context.
    /// The waiting is bounded, and giving up is not an error.
    /// </summary>
    [Fact]
    public void AWindowThatNeverAppearsIsGivenUpOn()
    {
        int pauses = 0;

        ConsoleWindow.Verdict verdict = ConsoleWindow.HideWhenReady(
            hasWindow: () => false,
            attachedProcesses: () => 0,
            hide: () => Assert.Fail("Hid a window that was never found."),
            pause: () => pauses++,
            attempts: 5);

        Assert.Equal(ConsoleWindow.Verdict.NotYet, verdict);
        Assert.Equal(5, pauses);
    }

    /// <summary>
    /// The ordinary case, and the one that must stay cheap: the window is there on the
    /// first look, so nothing waits.
    /// </summary>
    [Fact]
    public void AWindowThatIsAlreadyThereIsHiddenImmediately()
    {
        bool hidden = false;

        ConsoleWindow.Verdict verdict = ConsoleWindow.HideWhenReady(
            hasWindow: () => true,
            attachedProcesses: () => 1,
            hide: () => hidden = true,
            pause: () => Assert.Fail("Waited for a window that was already there."),
            attempts: 25);

        Assert.Equal(ConsoleWindow.Verdict.Hide, verdict);
        Assert.True(hidden);
    }

    /// <summary>
    /// Roughly two and a half seconds of looking, against a runtime that takes a
    /// quarter of one to start. Long enough to outlast a loaded machine, short enough
    /// that a thread is not left waiting on a window that is never coming.
    /// </summary>
    [Fact]
    public void TheWaitOutlastsAnySensibleStartup()
    {
        Assert.InRange(ConsoleWindow.Attempts * ConsoleWindow.PauseMilliseconds, 2000, 5000);
    }
}
