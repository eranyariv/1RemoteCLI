using OneRemoteCli.Daemon.Wrapper;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The code page decision, which is the part of entering a console that can be
/// reasoned about without one.
/// <para>
/// Everything else <c>Enter</c> does needs a real console attached to the test host,
/// which CI does not reliably provide, so the rule that decides whether to touch the
/// console at all is the piece pulled out and asserted on. It is the piece that
/// matters: getting it wrong either leaves a user's terminal on the wrong code page
/// after the session ends, or fails to fix the one case the whole change exists for.
/// </para>
/// </summary>
public class WindowsLocalTerminalTests
{
    /// <summary>
    /// The bug from issue #75. A console spawned by a desktop shortcut is on the system
    /// OEM code page, and the UTF-8 the wrapper passes through renders one glyph per
    /// byte — a box-drawing character arriving as three letters.
    /// </summary>
    [Theory]
    [InlineData(437u)]
    [InlineData(850u)]
    [InlineData(1252u)]
    [InlineData(932u)]
    public void AConsoleOnAnythingButUtf8IsChangedAndRemembered(uint codePage)
    {
        Assert.Equal(codePage, WindowsLocalTerminal.CodePageToRestore(codePage));
    }

    /// <summary>
    /// Nothing to change means nothing to restore. Claiming otherwise would have the
    /// wrapper set a console back to a value it never moved it away from.
    /// </summary>
    [Fact]
    public void AConsoleAlreadyOnUtf8IsLeftAlone()
    {
        Assert.Null(WindowsLocalTerminal.CodePageToRestore(65001));
    }

    /// <summary>
    /// Zero is what Windows reports when there is no console — output redirected to a
    /// file or a pipe, which is how the wrapper runs under test and in scripts.
    /// </summary>
    [Fact]
    public void NoConsoleMeansNoCodePageToChange()
    {
        Assert.Null(WindowsLocalTerminal.CodePageToRestore(0));
    }
}
