using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Protocol.Tests;

/// <summary>
/// The one field in the product a user types and a different device renders.
/// <para>
/// Worth its own file of tests for that reason alone. The name reaches a Web Push
/// notification on someone's lock screen and a terminal header on a machine the
/// author of the string is not standing at, so what survives this function is what
/// those surfaces are asked to draw.
/// </para>
/// </summary>
public class SessionNameTests
{
    [Fact]
    public void KeepsAnOrdinaryName()
    {
        Assert.Equal("The deploy", SessionName.Sanitize("The deploy"));
    }

    [Fact]
    public void TrimsTheEdges()
    {
        Assert.Equal("The deploy", SessionName.Sanitize("  The deploy  "));
    }

    /// <summary>Blank is not an empty name, it is the absence of one — which is how a name is cleared.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void TreatsBlankAsNoName(string? input)
    {
        Assert.Null(SessionName.Sanitize(input));
    }

    /// <summary>A name made only of things that do not print is blank, however long it was.</summary>
    [Fact]
    public void TreatsAnInvisibleNameAsNoName()
    {
        Assert.Null(SessionName.Sanitize("\u200b\u200b\u200b"));
    }

    /// <summary>
    /// The escape byte goes; the rest of the sequence stays as the printable text it
    /// is. Nothing here is trying to detect an ANSI sequence — remove the one
    /// character that can start one and the remainder is just letters.
    /// </summary>
    [Fact]
    public void DropsControlCharactersRatherThanLettingTheTerminalReadThem()
    {
        string? name = SessionName.Sanitize("\u001b[31mred");

        Assert.Equal("[31mred", name);
        Assert.DoesNotContain('\u001b', name!);
    }

    /// <summary>
    /// The bidi overrides, which are the reason this function exists at all: left in,
    /// they reverse the visible order of everything after them, so a name renders as
    /// something other than what it says.
    /// </summary>
    [Fact]
    public void DropsDirectionOverrides()
    {
        Assert.Equal("gpj.exe", SessionName.Sanitize("\u202egpj.exe"));
    }

    [Fact]
    public void DropsZeroWidthPadding()
    {
        Assert.Equal("deploy", SessionName.Sanitize("dep\u200bloy"));
    }

    /// <summary>A name pasted from two lines should read as two words, not one long one.</summary>
    [Fact]
    public void FoldsLineBreaksIntoSpaces()
    {
        Assert.Equal("build then deploy", SessionName.Sanitize("build\nthen\r\ndeploy"));
    }

    [Fact]
    public void CollapsesRunsOfWhitespace()
    {
        Assert.Equal("build deploy", SessionName.Sanitize("build     deploy"));
    }

    [Fact]
    public void KeepsEmojiAndNonLatinText()
    {
        Assert.Equal("🚀 פריסה", SessionName.Sanitize("🚀 פריסה"));
    }

    [Fact]
    public void TruncatesAnAbsurdName()
    {
        string? name = SessionName.Sanitize(new string('a', 200));

        Assert.Equal(SessionName.MaxLength, name!.Length);
    }

    /// <summary>
    /// Cutting by char rather than by text element would split the last emoji in half
    /// and leave a replacement glyph on whatever renders it.
    /// </summary>
    [Fact]
    public void TruncatesWithoutSplittingACharacter()
    {
        string? name = SessionName.Sanitize(string.Concat(Enumerable.Repeat("🚀", 100)));

        Assert.Equal(SessionName.MaxLength, name!.EnumerateRunes().Count());
        Assert.DoesNotContain('\ufffd', name);
    }

    [Fact]
    public void PrefersTheNameTheUserChose()
    {
        Assert.Equal("The deploy", SessionName.Best("The deploy", "PowerShell", "pwsh"));
    }

    /// <summary>Clearing a custom name reveals the agent's, which is the point of keeping both.</summary>
    [Fact]
    public void FallsBackToTheAgentName()
    {
        Assert.Equal("PowerShell", SessionName.Best(null, "PowerShell", "pwsh"));
    }

    [Fact]
    public void FallsBackToTheProgramWhenNothingElseIsSet()
    {
        Assert.Equal("pwsh", SessionName.Best(null, null, "pwsh"));
    }
}
