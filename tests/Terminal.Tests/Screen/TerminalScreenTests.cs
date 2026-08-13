using System.Text;

using OneRemoteCli.Terminal.Screen;

namespace OneRemoteCli.Terminal.Tests.Screen;

/// <summary>
/// What the screen model does with the sequences a session actually produces.
/// </summary>
public class TerminalScreenTests
{
    // Writing and wrapping.

    [Fact]
    public void TextLandsOnTheScreen()
    {
        var t = new ScreenHarness().Feed("hello");

        Assert.Equal("hello", t.Line(0));
        Assert.Equal((0, 5), t.Cursor);
    }

    [Fact]
    public void CarriageReturnAndLineFeedMoveAsTheyShould()
    {
        var t = new ScreenHarness().Feed("one\r\ntwo");

        Assert.Equal("one", t.Line(0));
        Assert.Equal("two", t.Line(1));
    }

    [Fact]
    public void TextLongerThanTheScreenWraps()
    {
        var t = new ScreenHarness(rows: 3, columns: 5).Feed("abcdefgh");

        Assert.Equal("abcde", t.Line(0));
        Assert.Equal("fgh", t.Line(1));
    }

    [Fact]
    public void FillingTheLastColumnDoesNotMoveToTheNextLineOnItsOwn()
    {
        // The pending-wrap rule. Writing the last cell of a line leaves the cursor on it,
        // so a program that fills a line and stops has not asked for a scroll.
        var t = new ScreenHarness(rows: 3, columns: 5).Feed("abcde");

        Assert.Equal((0, 4), t.Cursor);
        Assert.Equal("abcde", t.Line(0));
        Assert.Equal(string.Empty, t.Line(1));
    }

    [Fact]
    public void FillingTheLastColumnOfTheLastLineDoesNotScroll()
    {
        // The case pending-wrap exists for: eager wrapping here would silently discard
        // the top line of the screen.
        var t = new ScreenHarness(rows: 2, columns: 5).Feed("first\r\nabcde");

        Assert.Equal("first", t.Line(0));
        Assert.Equal("abcde", t.Line(1));
    }

    [Fact]
    public void OneMoreCharacterAfterAFullLineWrapsAndScrolls()
    {
        var t = new ScreenHarness(rows: 2, columns: 5).Feed("first\r\nabcdeX");

        Assert.Equal("abcde", t.Line(0));
        Assert.Equal("X", t.Line(1));
    }

    [Fact]
    public void WithAutowrapOffTextPilesUpInTheLastColumn()
    {
        var t = new ScreenHarness(rows: 3, columns: 5).Feed("\u001b[?7labcdefgh");

        Assert.Equal("abcdh", t.Line(0));
        Assert.Equal(string.Empty, t.Line(1));
    }

    [Fact]
    public void ScrollingPastTheBottomDropsTheTopLine()
    {
        // No scrollback: what leaves the top of the screen is gone. That is the trade
        // that keeps a session small enough to snapshot over a phone connection.
        var t = new ScreenHarness(rows: 3, columns: 10).Feed("a\r\nb\r\nc\r\nd");

        Assert.Equal(["b", "c", "d"], t.Lines());
    }

    // Cursor addressing.

    [Fact]
    public void CursorPositionIsOneBasedOnTheWire()
    {
        var t = new ScreenHarness().Feed("\u001b[3;5Hx");

        Assert.Equal("    x", t.Line(2));
    }

    [Fact]
    public void CursorPositionWithNoParametersHomesTheCursor()
    {
        var t = new ScreenHarness().Feed("\u001b[5;5H\u001b[Hx");

        Assert.Equal("x", t.Line(0));
    }

    [Fact]
    public void RelativeCursorMovesAreClampedToTheScreen()
    {
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("\u001b[99A\u001b[99Dx");

        Assert.Equal("x", t.Line(0));
    }

    [Fact]
    public void BackspaceMovesLeftWithoutErasing()
    {
        var t = new ScreenHarness().Feed("abc\b\bX");

        Assert.Equal("aXc", t.Line(0));
    }

    [Fact]
    public void TabsGoToEveryEighthColumn()
    {
        var t = new ScreenHarness(rows: 2, columns: 30).Feed("a\tb\tc");

        Assert.Equal("a       b       c", t.Line(0));
    }

    [Fact]
    public void ACustomTabStopIsHonouredAndCanBeCleared()
    {
        var t = new ScreenHarness(rows: 2, columns: 30)
            .Feed("\u001b[4G\u001bH")   // set a stop at column 4
            .Feed("\u001b[1G\ta");

        Assert.Equal("   a", t.Line(0));

        // With every stop cleared, a tab runs to the end of the line instead.
        t.Feed("\u001b[1G\u001b[3g\tb");

        Assert.Equal("b", t.CellAt(0, 29).Text);
    }

    [Fact]
    public void SaveAndRestoreBringBackPositionAndColour()
    {
        var t = new ScreenHarness()
            .Feed("\u001b[31m\u001b[2;3H\u001b7")
            .Feed("\u001b[0m\u001b[5;1Hxxx")
            .Feed("\u001b8Y");

        Assert.Equal("  Y", t.Line(1));
        Assert.Equal(VtColor.FromIndex(1), t.AttributesAt(1, 2).Foreground);
    }

    // Erasing.

    [Fact]
    public void EraseToEndOfLineLeavesWhatCameBefore()
    {
        var t = new ScreenHarness().Feed("abcdef\u001b[3G\u001b[K");

        Assert.Equal("ab", t.Line(0));
    }

    [Fact]
    public void EraseToStartOfLineClearsTheCursorCellToo()
    {
        var t = new ScreenHarness().Feed("abcdef\u001b[3G\u001b[1K");

        Assert.Equal("   def", t.Line(0));
    }

    [Fact]
    public void EraseDisplayClearsEverythingAndLeavesTheCursorAlone()
    {
        var t = new ScreenHarness().Feed("a\r\nb\r\nc\u001b[2;2H\u001b[2J");

        Assert.All(t.Lines(), line => Assert.Equal(string.Empty, line));
        Assert.Equal((1, 1), t.Cursor);
    }

    [Fact]
    public void ErasingKeepsTheCurrentBackgroundColour()
    {
        // Background-colour erase. Clearing a line inside a coloured panel has to leave
        // the panel's colour behind, not a hole.
        var t = new ScreenHarness().Feed("\u001b[44m\u001b[2J");

        Assert.Equal(VtColor.FromIndex(4), t.AttributesAt(2, 5).Background);
        Assert.Equal(VtColor.Default, t.AttributesAt(2, 5).Foreground);
    }

    [Fact]
    public void ErasingCharactersLeavesTheRestOfTheLineInPlace()
    {
        var t = new ScreenHarness().Feed("abcdef\u001b[2G\u001b[3X");

        Assert.Equal("a   ef", t.Line(0));
    }

    // Line and character editing.

    [Fact]
    public void InsertingCharactersPushesTheRestOfTheLineRight()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("abcdef\u001b[3G\u001b[2@");

        Assert.Equal("ab  cdef", t.Line(0));
    }

    [Fact]
    public void DeletingCharactersPullsTheRestOfTheLineLeft()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("abcdef\u001b[3G\u001b[2P");

        Assert.Equal("abef", t.Line(0));
    }

    [Fact]
    public void InsertingLinesPushesTheOnesBelowDown()
    {
        var t = new ScreenHarness(rows: 4, columns: 10).Feed("a\r\nb\r\nc\u001b[2;1H\u001b[L");

        Assert.Equal(["a", "", "b", "c"], t.Lines());
    }

    [Fact]
    public void DeletingLinesPullsTheOnesBelowUp()
    {
        var t = new ScreenHarness(rows: 4, columns: 10).Feed("a\r\nb\r\nc\u001b[2;1H\u001b[M");

        Assert.Equal(["a", "c", "", ""], t.Lines());
    }

    [Fact]
    public void InsertModeShiftsInsteadOfOverwriting()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("abcd\u001b[1G\u001b[4hXY");

        Assert.Equal("XYabcd", t.Line(0));
    }

    // Scroll regions.

    [Fact]
    public void AScrollRegionConfinesScrollingToItsRows()
    {
        // The status-bar idiom: a fixed line at the bottom that scrolling must not touch.
        var t = new ScreenHarness(rows: 5, columns: 10)
            .Feed("\u001b[5;1Hstatus")
            .Feed("\u001b[1;4r")
            .Feed("\u001b[4;1Ha\r\nb\r\nc\r\nd\r\ne");

        Assert.Equal(["b", "c", "d", "e", "status"], t.Lines());
    }

    [Fact]
    public void SettingAScrollRegionHomesTheCursor()
    {
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("\u001b[5;5H\u001b[2;4rx");

        Assert.Equal("x", t.Line(0));
    }

    [Fact]
    public void AnInvertedScrollRegionIsIgnored()
    {
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("\u001b[4;2r");

        Assert.Equal(0, t.Screen.ScrollTop);
        Assert.Equal(4, t.Screen.ScrollBottom);
    }

    [Fact]
    public void OriginModeMakesRowOneTheTopOfTheRegion()
    {
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("\u001b[2;4r\u001b[?6h\u001b[1;1Hx");

        Assert.Equal("x", t.Line(1));
    }

    [Fact]
    public void ReverseIndexAtTheTopOfTheRegionScrollsItDown()
    {
        var t = new ScreenHarness(rows: 4, columns: 10).Feed("a\r\nb\r\nc\u001b[1;1H\u001bM");

        Assert.Equal(["", "a", "b", "c"], t.Lines());
    }

    // Colours and attributes.

    [Fact]
    public void BasicColoursApply()
    {
        var t = new ScreenHarness().Feed("\u001b[31;42mx");

        Assert.Equal(VtColor.FromIndex(1), t.AttributesAt(0, 0).Foreground);
        Assert.Equal(VtColor.FromIndex(2), t.AttributesAt(0, 0).Background);
    }

    [Fact]
    public void BrightColoursMapToTheUpperHalfOfThePalette()
    {
        var t = new ScreenHarness().Feed("\u001b[91mx");

        Assert.Equal(VtColor.FromIndex(9), t.AttributesAt(0, 0).Foreground);
    }

    [Fact]
    public void TwoHundredAndFiftySixColourIndexesApply()
    {
        var t = new ScreenHarness().Feed("\u001b[38;5;208mx");

        Assert.Equal(VtColor.FromIndex(208), t.AttributesAt(0, 0).Foreground);
    }

    [Fact]
    public void TrueColourAppliesInTheSemicolonForm()
    {
        var t = new ScreenHarness().Feed("\u001b[38;2;255;128;0mx");

        Assert.Equal(VtColor.FromRgb(255, 128, 0), t.AttributesAt(0, 0).Foreground);
    }

    [Fact]
    public void TrueColourAppliesInTheColonForm()
    {
        // The correct spelling, and the one that tells the parser's grouping apart from
        // five separate attributes.
        var t = new ScreenHarness().Feed("\u001b[38:2::255:128:0mx");

        Assert.Equal(VtColor.FromRgb(255, 128, 0), t.AttributesAt(0, 0).Foreground);
    }

    [Fact]
    public void TrueColourAppliesInTheColonFormWithoutAColourSpace()
    {
        var t = new ScreenHarness().Feed("\u001b[38:2:255:128:0mx");

        Assert.Equal(VtColor.FromRgb(255, 128, 0), t.AttributesAt(0, 0).Foreground);
    }

    [Fact]
    public void AttributesAfterATrueColourAreNotSwallowed()
    {
        // The semicolon form is ambiguous: its arguments look exactly like more
        // attributes. Getting the skip wrong makes everything after a colour vanish.
        var t = new ScreenHarness().Feed("\u001b[38;2;10;20;30;1mx");

        Assert.Equal(VtColor.FromRgb(10, 20, 30), t.AttributesAt(0, 0).Foreground);
        Assert.True(t.AttributesAt(0, 0).Has(CellFlags.Bold));
    }

    [Fact]
    public void AttributesTurnOnAndOffIndependently()
    {
        var t = new ScreenHarness().Feed("\u001b[1;3;4mx\u001b[24my");

        Assert.True(t.AttributesAt(0, 0).Has(CellFlags.Bold | CellFlags.Italic));
        Assert.True(t.AttributesAt(0, 0).Has(CellFlags.Underline));
        Assert.False(t.AttributesAt(0, 1).Has(CellFlags.Underline));
        Assert.True(t.AttributesAt(0, 1).Has(CellFlags.Bold));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var t = new ScreenHarness().Feed("\u001b[1;31;44mx\u001b[0my");

        Assert.Equal(CellAttributes.Default, t.AttributesAt(0, 1));
    }

    [Fact]
    public void UnderlineStyleZeroTurnsUnderliningOff()
    {
        var t = new ScreenHarness().Feed("\u001b[4mx\u001b[4:0my");

        Assert.True(t.AttributesAt(0, 0).Has(CellFlags.Underline));
        Assert.False(t.AttributesAt(0, 1).Has(CellFlags.Underline));
    }

    // Alternate screen.

    [Fact]
    public void TheAlternateScreenHidesThePrimaryAndGivesItBack()
    {
        // Every full-screen program does this. Getting it wrong means attaching to an
        // editor shows the shell that launched it.
        var t = new ScreenHarness(rows: 3, columns: 10)
            .Feed("shell prompt")
            .Feed("\u001b[?1049h\u001b[H")
            .Feed("editor");

        Assert.True(t.Screen.IsAlternateScreen);
        Assert.Equal("editor", t.Line(0));

        t.Feed("\u001b[?1049l");

        Assert.False(t.Screen.IsAlternateScreen);
        Assert.Equal("shell prom", t.Line(0));
    }

    [Fact]
    public void LeavingTheAlternateScreenPutsTheCursorBack()
    {
        var t = new ScreenHarness(rows: 5, columns: 10)
            .Feed("\u001b[3;5H")
            .Feed("\u001b[?1049h\u001b[1;1Hstuff\u001b[?1049l");

        Assert.Equal((2, 4), t.Cursor);
    }

    [Fact]
    public void TheAlternateScreenStartsEmptyEachTime()
    {
        var t = new ScreenHarness(rows: 3, columns: 10)
            .Feed("\u001b[?1049hleftover\u001b[?1049l")
            .Feed("\u001b[?1049h");

        Assert.Equal(string.Empty, t.Line(0));
    }

    [Fact]
    public void WritingToOneScreenDoesNotTouchTheOther()
    {
        var t = new ScreenHarness(rows: 3, columns: 10)
            .Feed("primary")
            .Feed("\u001b[?1049h\u001b[2Jalternate\u001b[?1049l");

        Assert.Equal("primary", t.Line(0));
    }

    // Modes.

    [Fact]
    public void CursorVisibilityIsTracked()
    {
        var t = new ScreenHarness();

        Assert.True(t.Screen.Modes.CursorVisible);

        t.Feed("\u001b[?25l");
        Assert.False(t.Screen.Modes.CursorVisible);

        t.Feed("\u001b[?25h");
        Assert.True(t.Screen.Modes.CursorVisible);
    }

    [Fact]
    public void InputAffectingModesAreTrackedSoASnapshotCanRestoreThem()
    {
        // A phone attaching to a program that turned on bracketed paste has to be told,
        // or its first paste arrives unbracketed and the program mis-reads it.
        var t = new ScreenHarness().Feed("\u001b[?1h\u001b[?2004h\u001b[?1002h\u001b[?1006h");

        Assert.True(t.Screen.Modes.ApplicationCursorKeys);
        Assert.True(t.Screen.Modes.BracketedPaste);
        Assert.True(t.Screen.Modes.MouseDragTracking);
        Assert.True(t.Screen.Modes.SgrMouseEncoding);
    }

    [Fact]
    public void SeveralModesCanBeSetInOneSequence()
    {
        var t = new ScreenHarness().Feed("\u001b[?25;2004l");

        Assert.False(t.Screen.Modes.CursorVisible);
        Assert.False(t.Screen.Modes.BracketedPaste);
    }

    // Title.

    [Fact]
    public void TheTitleIsTakenFromBothTitleSequences()
    {
        Assert.Equal("one", new ScreenHarness().Feed("\u001b]0;one\u0007").Screen.Title);
        Assert.Equal("two", new ScreenHarness().Feed("\u001b]2;two\u001b\\").Screen.Title);
    }

    [Fact]
    public void OtherOscSequencesLeaveTheTitleAlone()
    {
        var t = new ScreenHarness().Feed("\u001b]0;kept\u0007\u001b]52;c;aGk=\u0007");

        Assert.Equal("kept", t.Screen.Title);
    }

    [Fact]
    public void ControlCharactersAreStrippedFromTheTitle()
    {
        // A title ends up in a UI. Anything that can carry an escape sequence into one
        // does not belong in it.
        var t = new ScreenHarness().Feed("\u001b]0;ti\u0001tle\u0007");

        Assert.Equal("title", t.Screen.Title);
    }

    // Character sets.

    [Fact]
    public void TheLineDrawingCharacterSetProducesBoxes()
    {
        var t = new ScreenHarness().Feed("\u001b(0lqk\u001b(Bx");

        Assert.Equal("┌─┐x", t.Line(0));
    }

    [Fact]
    public void ShiftOutSelectsTheOtherCharacterSet()
    {
        var t = new ScreenHarness().Feed("\u001b)0\u000eq\u000fq");

        Assert.Equal("─q", t.Line(0));
    }

    // Unicode.

    [Fact]
    public void WideCharactersTakeTwoColumns()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("日本語");

        Assert.Equal((0, 6), t.Cursor);
        Assert.Equal("日本語", t.Line(0));
    }

    [Fact]
    public void OverwritingHalfOfAWideCharacterRemovesAllOfIt()
    {
        // Leaving the other half behind would desynchronise every column after it.
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("日本\u001b[1Gx");

        Assert.Equal("x 本", t.Line(0));
    }

    [Fact]
    public void AWideCharacterThatDoesNotFitMovesToTheNextLine()
    {
        var t = new ScreenHarness(rows: 3, columns: 5).Feed("abcd日");

        Assert.Equal("abcd", t.Line(0));
        Assert.Equal("日", t.Line(1));
    }

    [Fact]
    public void CombiningMarksStayWithTheCharacterTheyModify()
    {
        // Giving them their own cell would shift every column after them. The cell holds
        // the decomposed form it was sent, not a normalised one — normalising here would
        // make the model disagree with the terminal it is mirroring.
        var t = new ScreenHarness().Feed("e\u0301x");

        Assert.Equal("e\u0301x", t.Line(0));
        Assert.Equal("e\u0301", t.CellAt(0, 0).Text);
        Assert.Equal((0, 2), t.Cursor);
    }

    [Fact]
    public void EmojiTakeTwoColumns()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("😀ok");

        Assert.Equal((0, 4), t.Cursor);
        Assert.Equal("😀ok", t.Line(0));
    }

    [Fact]
    public void BoxDrawingAndBrailleStayNarrow()
    {
        // Spinners and borders are the most common non-ASCII output these CLIs produce,
        // and treating them as wide would shift every prompt sideways.
        var t = new ScreenHarness(rows: 2, columns: 20).Feed("┌─┐⠋✓");

        Assert.Equal((0, 5), t.Cursor);
    }

    // Resize.

    [Fact]
    public void GrowingTheScreenKeepsWhatWasThere()
    {
        var t = new ScreenHarness(rows: 3, columns: 10).Feed("a\r\nb\r\nc");
        t.Screen.Resize(5, 20);

        Assert.Equal(["a", "b", "c", "", ""], t.Lines());
    }

    [Fact]
    public void ShrinkingAMostlyEmptyScreenKeepsTheContent()
    {
        // Dropping from the top here would erase everything the user has.
        var t = new ScreenHarness(rows: 10, columns: 10).Feed("a\r\nb\r\nc");
        t.Screen.Resize(4, 10);

        Assert.Equal(["a", "b", "c", ""], t.Lines());
        Assert.Equal((2, 1), t.Cursor);
    }

    [Fact]
    public void ShrinkingAFullScreenKeepsTheBottom()
    {
        // And dropping from the bottom here would erase the prompt.
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("a\r\nb\r\nc\r\nd\r\ne");
        t.Screen.Resize(3, 10);

        Assert.Equal(["c", "d", "e"], t.Lines());
        Assert.Equal((2, 1), t.Cursor);
    }

    [Fact]
    public void NarrowingTruncatesRatherThanReflowing()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("abcdefghij");
        t.Screen.Resize(2, 5);

        Assert.Equal("abcde", t.Line(0));
        Assert.Equal(string.Empty, t.Line(1));
    }

    [Fact]
    public void NarrowingDoesNotLeaveHalfOfAWideCharacterBehind()
    {
        var t = new ScreenHarness(rows: 2, columns: 10).Feed("abcd日本");
        t.Screen.Resize(2, 5);

        Assert.Equal("abcd", t.Line(0));
    }

    [Fact]
    public void ResizingClearsAStaleScrollRegion()
    {
        // A region pinned to the old size confines every later scroll to part of the
        // screen, which looks like output mysteriously stopping.
        var t = new ScreenHarness(rows: 10, columns: 10).Feed("\u001b[1;5r");
        t.Screen.Resize(20, 10);

        Assert.Equal(0, t.Screen.ScrollTop);
        Assert.Equal(19, t.Screen.ScrollBottom);
    }

    [Fact]
    public void ResizingKeepsBothBuffersTheSameSize()
    {
        var t = new ScreenHarness(rows: 5, columns: 10).Feed("\u001b[?1049h");
        t.Screen.Resize(8, 30);

        Assert.Equal(8, t.Screen.Rows);
        Assert.Equal(30, t.Screen.Columns);

        t.Feed("\u001b[?1049l");

        Assert.Equal(8, t.Screen.Rows);
        Assert.Equal(30, t.Screen.Columns);
        Assert.Equal(8, t.Lines().Length);
    }

    // Reset.

    [Fact]
    public void AFullResetReturnsEverythingToItsStartingState()
    {
        var t = new ScreenHarness(rows: 5, columns: 10)
            .Feed("\u001b[?1049h\u001b[31;1mmess\u001b[?25l\u001b]0;title\u0007")
            .Feed("\u001bc");

        Assert.False(t.Screen.IsAlternateScreen);
        Assert.True(t.Screen.Modes.CursorVisible);
        Assert.Equal(string.Empty, t.Screen.Title);
        Assert.Equal(CellAttributes.Default, t.Screen.CurrentAttributes);
        Assert.Equal((0, 0), t.Cursor);
        Assert.All(t.Lines(), line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void TheAlignmentTestFillsTheScreen()
    {
        var t = new ScreenHarness(rows: 3, columns: 4).Feed("\u001b#8");

        Assert.Equal(["EEEE", "EEEE", "EEEE"], t.Lines());
    }

    // Robustness.

    [Fact]
    public void QueriesAreIgnoredRatherThanPrinted()
    {
        // This emulator watches a stream the real console has already answered. Replying
        // would mean writing bytes into the session's input that nothing asked for.
        var t = new ScreenHarness().Feed("\u001b[c\u001b[6n\u001b[0cok");

        Assert.Equal("ok", t.Line(0));
    }

    [Fact]
    public void DeviceControlPayloadsDoNotReachTheScreen()
    {
        var t = new ScreenHarness().Feed("\u001bP1$r0m\u001b\\ok");

        Assert.Equal("ok", t.Line(0));
    }

    [Fact]
    public void AStreamOfArbitrarySequencesLeavesAConsistentScreen()
    {
        var random = new Random(20260105);
        var screen = new TerminalScreen(24, 80);
        var parser = new OneRemoteCli.Terminal.Vt.VtParser();

        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(1, 64)];
            random.NextBytes(bytes);
            parser.Parse(bytes, screen);
        }

        Assert.Equal(24, screen.Rows);
        Assert.InRange(screen.CursorRow, 0, screen.Rows - 1);
        Assert.InRange(screen.CursorColumn, 0, screen.Columns - 1);
        Assert.InRange(screen.ScrollTop, 0, screen.Rows - 1);
        Assert.InRange(screen.ScrollBottom, screen.ScrollTop, screen.Rows - 1);

        // Every wide character still has both halves, and neither half is orphaned.
        for (int y = 0; y < screen.Rows; y++)
        {
            ReadOnlySpan<Cell> row = screen.GetRow(y);

            for (int x = 0; x < screen.Columns; x++)
            {
                if (row[x].IsWideLeading)
                {
                    Assert.True(x + 1 < screen.Columns && row[x + 1].IsWideTrailing, $"orphan lead at {y},{x}");
                }

                if (row[x].IsWideTrailing)
                {
                    Assert.True(x > 0 && row[x - 1].IsWideLeading, $"orphan trail at {y},{x}");
                }
            }
        }
    }

    [Fact]
    public void AScreenFitsWellInsideThePerSessionMemoryBudget()
    {
        // Two buffers of the largest screen a phone will ever attach to. The budget is
        // 2 MB per session and the rest of the session needs room too.
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var screens = new TerminalScreen[10];

        for (int i = 0; i < screens.Length; i++)
        {
            screens[i] = new TerminalScreen(60, 250);
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long perScreen = (after - before) / screens.Length;

        Assert.InRange(perScreen, 0, 2 * 1024 * 1024);
        Assert.Equal(60, screens[^1].Rows);
    }
}
