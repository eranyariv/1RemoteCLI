using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Putting the agent's directory on the user's PATH, and taking it off again.
/// <para>
/// Only the string handling is exercised, and deliberately so: this is code whose
/// failure mode is destroying a PATH the user spent years accumulating, and a test
/// that wrote to the real one would be doing exactly the damage it is meant to
/// prevent on whichever machine happened to run it.
/// </para>
/// </summary>
public sealed class PathEntryTests
{
    private const string Directory = @"C:\Users\someone\AppData\Local\Programs\1RemoteCLI";

    [Fact]
    public void AppendsToWhatIsAlreadyThere()
    {
        string? updated = PathEntry.Adding(@"C:\Windows;C:\Windows\System32", Directory);

        Assert.Equal($@"C:\Windows;C:\Windows\System32;{Directory}", updated);
    }

    /// <summary>
    /// The reason the whole thing reads the value unexpanded. These entries have to
    /// come out the far side still written as variables, or the user's PATH quietly
    /// stops following their profile around.
    /// </summary>
    [Fact]
    public void LeavesUnexpandedVariablesAlone()
    {
        const string current = @"%USERPROFILE%\.dotnet\tools;%LOCALAPPDATA%\Microsoft\WindowsApps";

        string? updated = PathEntry.Adding(current, Directory);

        Assert.StartsWith(current, updated, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", updated, StringComparison.Ordinal);
    }

    /// <summary>
    /// Installing twice is an ordinary thing to do — upgrades run it again — and it
    /// must not leave the directory on the PATH twice.
    /// </summary>
    [Fact]
    public void SaysNothingNeedsDoingWhenItIsAlreadyThere()
    {
        Assert.Null(PathEntry.Adding($@"C:\Windows;{Directory}", Directory));
    }

    /// <summary>Windows does not care about case or a trailing separator, so neither can this.</summary>
    [Theory]
    [InlineData(@"c:\users\someone\appdata\local\programs\1remotecli")]
    [InlineData(Directory + @"\")]
    public void RecognisesTheSameDirectorySpeltDifferently(string existing)
    {
        Assert.Null(PathEntry.Adding($@"C:\Windows;{existing}", Directory));
    }

    [Fact]
    public void CopesWithAProfileThatHasNoPathOfItsOwn()
    {
        Assert.Equal(Directory, PathEntry.Adding(string.Empty, Directory));
    }

    /// <summary>A stray trailing separator is common and must not produce an empty entry.</summary>
    [Fact]
    public void DoesNotLeaveAnEmptyEntryBehindATrailingSeparator()
    {
        string? updated = PathEntry.Adding(@"C:\Windows;", Directory);

        Assert.Equal($@"C:\Windows;{Directory}", updated);
    }

    [Fact]
    public void TakesOnlyItsOwnEntryBackOut()
    {
        string? updated = PathEntry.Removing($@"C:\Windows;{Directory};C:\Tools", Directory);

        Assert.Equal(@"C:\Windows;C:\Tools", updated);
    }

    [Fact]
    public void SaysNothingNeedsDoingWhenItWasNeverThere()
    {
        Assert.Null(PathEntry.Removing(@"C:\Windows;C:\Tools", Directory));
    }

    /// <summary>
    /// Uninstalling must not be the thing that flattens the variables either, so the
    /// entries either side come back exactly as they went in.
    /// </summary>
    [Fact]
    public void RemovalLeavesTheRestUntouched()
    {
        string? updated = PathEntry.Removing($@"%USERPROFILE%\bin;{Directory};%LOCALAPPDATA%\bin", Directory);

        Assert.Equal(@"%USERPROFILE%\bin;%LOCALAPPDATA%\bin", updated);
    }
}
