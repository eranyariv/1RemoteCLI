using System.Text;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// Turns a <see cref="TerminalScreen"/> back into the VT byte stream that reproduces it.
/// <para>
/// This is what makes an attach cheap for the client: a snapshot and a live delta are
/// the same kind of thing — bytes to write to a terminal — so the client needs no
/// snapshot decoder, no second rendering path, and no way for the two paths to disagree
/// about how something looks.
/// </para>
/// <para>
/// The output reproduces <em>semantics</em>, not the original bytes. It will look nothing
/// like what the program actually emitted: a screen built by a thousand incremental
/// cursor moves comes back as a straightforward top-to-bottom repaint. What matters is
/// that feeding this to a fresh terminal of the same size lands it in the same state,
/// which is exactly the property the round-trip tests assert.
/// </para>
/// <para>
/// The screen size is not part of the stream. A client is resized to the session's
/// dimensions before a snapshot is applied, because there is no escape sequence a
/// terminal is obliged to honour for it and guessing wrong would reflow everything.
/// </para>
/// </summary>
public static class VtSnapshotWriter
{
    private const char Esc = '\u001b';

    /// <summary>The snapshot as UTF-8 bytes, ready to be framed and sent.</summary>
    public static byte[] Serialize(TerminalScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        return Encoding.UTF8.GetBytes(SerializeToString(screen));
    }

    /// <summary>The snapshot as text. Useful in tests and for logging; the wire uses <see cref="Serialize"/>.</summary>
    public static string SerializeToString(TerminalScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // Roughly one byte per cell for a typical mostly-blank screen, plus room for the
        // per-row positioning. Over-allocating here is far cheaper than regrowing.
        var output = new StringBuilder(screen.Rows * (screen.Columns + 8));

        // Start from a state both sides agree on. Everything after this is a delta from
        // power-on, which is what lets the rest of the writer emit only what differs.
        WritePowerOnState(output, screen);

        // The pen is what the receiving terminal's SGR state will be, tracked so a run
        // that matches the previous one costs nothing.
        var pen = CellAttributes.Default;

        PaintBuffer(output, screen.PrimaryBuffer, ref pen);

        if (screen.IsAlternateScreen)
        {
            // The primary screen's saved cursor is what the shell gets back when the
            // full-screen program exits, so it has to be planted before the switch.
            // This one is unconditional: leaving the cursor wherever the repaint ended
            // would save a position the session never had.
            PlantSavedCursor(output, screen.SavedPrimaryCursor, ref pen, always: true);

            // Back to power-on rendition *before* the switch, because switching clears
            // the alternate screen with the current background colour. Leaving the
            // shell's colour in force here would tint every cell the repaint then skips.
            ResetPaintingState(output, screen.SavedPrimaryCursor, ref pen);

            // 1047 rather than 1049: the saved cursor was already planted by the DECSC
            // above, and 1049 would overwrite it with whatever is current now.
            output.Append(Esc).Append("[?1047h");

            PaintBuffer(output, screen.AlternateBuffer, ref pen);
            PlantSavedCursor(output, screen.SavedAlternateCursor, ref pen, always: false);
            ResetPaintingState(output, screen.SavedAlternateCursor, ref pen);
        }
        else
        {
            // The alternate buffer keeps its contents when a program leaves it without
            // clearing, and DECSET 47 can bring them back into view. Reproducing that
            // costs nothing in practice — a program that exits properly clears it, so
            // this is skipped every time it does not matter.
            if (HasContent(screen.AlternateBuffer) || screen.SavedAlternateCursor != default)
            {
                output.Append(Esc).Append("[?47h");
                PaintBuffer(output, screen.AlternateBuffer, ref pen);
                PlantSavedCursor(output, screen.SavedAlternateCursor, ref pen, always: false);
                ResetPaintingState(output, screen.SavedAlternateCursor, ref pen);
                output.Append(Esc).Append("[?47l");
            }

            PlantSavedCursor(output, screen.SavedPrimaryCursor, ref pen, always: false);
            ResetPaintingState(output, screen.SavedPrimaryCursor, ref pen);
        }

        WriteTabStops(output, screen);
        WriteScrollRegion(output, screen);
        WriteModes(output, screen);
        WriteCharsets(output, screen.G0, screen.G1, screen.ActiveCharset);
        WriteSgr(output, pen, screen.CurrentAttributes);
        WriteCursorPosition(output, screen);
        WriteCursorAppearance(output, screen);
        WriteTitle(output, screen.Title);

        return output.ToString();
    }

    /// <summary>
    /// Puts the receiving terminal into power-on state without discarding its scrollback.
    /// <para>
    /// This used to be <c>ESC c</c>, one byte that did the whole job. RIS also throws
    /// away the client's scrollback, though, and on a phone that is the only history
    /// there is: the agent models the visible screen and nothing above it, so lines
    /// that scrolled off exist solely in the emulator the user is looking at. A snapshot
    /// arrives on every reattach the tail is too small to answer — which, for a CLI that
    /// repaints, is every reattach — so RIS was deleting an hour of real output to
    /// redraw a screen that was already correct.
    /// </para>
    /// <para>
    /// Erasing the display instead leaves the scrollback where it is. Everything RIS
    /// reset for free then has to be said out loud, which is what the rest of this
    /// method is. That keeps the property the writer is built on — every later
    /// <c>Write*</c> emits only what differs from power-on — so the cost of the change
    /// is paid here once rather than spread across all of them.
    /// </para>
    /// </summary>
    private static void WritePowerOnState(StringBuilder output, TerminalScreen screen)
    {
        // The pen and the charsets come first because everything below depends on them:
        // an erase fills with the current background, so a client that had a colour set
        // would otherwise be cleared to that colour rather than to blank.
        output.Append(Esc).Append("[0m");
        WriteCharsets(output, Charset.Ascii, Charset.Ascii, 0);

        // Absolute addressing, full-height scrolling, no insert, wrap on. The repaint
        // positions every row explicitly, and origin mode or a leftover scroll region
        // would silently shift all of it.
        output.Append(Esc).Append("[?6l");
        output.Append(Esc).Append("[r");
        output.Append(Esc).Append("[4l");
        output.Append(Esc).Append("[?7h");

        // The alternate buffer, cleared through a bare 47 switch rather than 1047 or
        // 1049: those clear and move the cursor themselves, and the point here is to do
        // exactly one known thing per sequence. The alternate screen has no scrollback
        // to protect, but it does hold the previous session's editor.
        output.Append(Esc).Append("[?47h");
        output.Append(Esc).Append("[2J");
        output.Append(Esc).Append("[H");
        output.Append(Esc).Append('7');
        output.Append(Esc).Append("[?47l");

        // The primary screen. ED 2 clears the rows in view and leaves everything above
        // them alone, which is the whole point of this method.
        output.Append(Esc).Append("[2J");
        output.Append(Esc).Append("[H");
        output.Append(Esc).Append('7');

        // Tab stops every eight columns. There is no sequence that restores the default
        // set, so it is cleared and rebuilt; RIS was doing this invisibly.
        output.Append(Esc).Append("[3g");

        for (int column = 8; column < screen.Columns; column += 8)
        {
            output.Append(Esc).Append('[').Append(column + 1).Append('G');
            output.Append(Esc).Append('H');
        }

        output.Append(Esc).Append("[H");

        // The modes whose power-on value is "off", plus the two whose power-on value is
        // "on". Written unconditionally, because what the client has cannot be known.
        output.Append(Esc).Append("[?1l");
        output.Append(Esc).Append('>');
        output.Append(Esc).Append("[?2004l");
        output.Append(Esc).Append("[?1000l");
        output.Append(Esc).Append("[?1002l");
        output.Append(Esc).Append("[?1003l");
        output.Append(Esc).Append("[?1006l");
        output.Append(Esc).Append("[?1004l");
        output.Append(Esc).Append("[?25h");
        output.Append(Esc).Append("[?12h");
        output.Append(Esc).Append("[0 q");

        // An empty title. A stale one names the session the user was in last, which is
        // the one thing a title must never do.
        output.Append(Esc).Append("]0;").Append('');
    }

    // Painting.

    /// <summary>
    /// Repaints one buffer, one row at a time.
    /// <para>
    /// Every row is positioned explicitly rather than relying on wrapping to carry the
    /// cursor onward. Wrapping would make the output depend on the receiving terminal's
    /// autowrap state and on whether the previous row happened to be full, and a single
    /// row that was one character short would shift everything below it.
    /// </para>
    /// </summary>
    private static void PaintBuffer(StringBuilder output, ScreenBuffer buffer, ref CellAttributes pen)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            ReadOnlySpan<Cell> cells = buffer.GetRow(row);
            int end = ContentEnd(cells);

            if (end == 0)
            {
                // Nothing here that the reset did not already produce.
                continue;
            }

            output.Append(Esc).Append('[').Append(row + 1).Append(";1H");

            int column = 0;
            while (column < end)
            {
                Cell cell = cells[column];

                if (cell.IsWideTrailing && column > 0 && cells[column - 1].IsWideLeading)
                {
                    // The character that owns this column was written with its left half.
                    column++;
                    continue;
                }

                int skipped = BlankRunLength(cells, column, end);

                // Three is where skipping starts paying: a CUF costs four bytes, so a
                // shorter gap is cheaper as spaces — and spaces avoid a pen change when
                // the pen is not already at the default.
                if (skipped >= 3)
                {
                    output.Append(Esc).Append('[').Append(skipped).Append('C');
                    column += skipped;
                    continue;
                }

                WriteSgr(output, pen, cell.Attributes.Rendition);
                pen = cell.Attributes.Rendition;
                AppendCellText(output, cell);
                column++;
            }
        }
    }

    /// <summary>True when a buffer holds anything a terminal reset would not produce.</summary>
    private static bool HasContent(ScreenBuffer buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            if (ContentEnd(buffer.GetRow(row)) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One past the last column worth emitting.
    /// <para>
    /// Trailing cells are only droppable when they carry no colour: a run of blanks with
    /// a background set is a coloured bar, and trimming it would cut the bar short.
    /// </para>
    /// </summary>
    private static int ContentEnd(ReadOnlySpan<Cell> cells)
    {
        for (int column = cells.Length - 1; column >= 0; column--)
        {
            if (!IsDefaultBlank(cells[column]))
            {
                return column + 1;
            }
        }

        return 0;
    }

    /// <summary>How many columns from <paramref name="start"/> the reset already produced.</summary>
    private static int BlankRunLength(ReadOnlySpan<Cell> cells, int start, int end)
    {
        int column = start;

        while (column < end && IsDefaultBlank(cells[column]))
        {
            column++;
        }

        return column - start;
    }

    /// <summary>True when a cell is indistinguishable from one the terminal reset produced.</summary>
    private static bool IsDefaultBlank(Cell cell) =>
        cell.IsBlank && cell.Attributes.Rendition == CellAttributes.Default;

    private static void AppendCellText(StringBuilder output, Cell cell)
    {
        string text = cell.Text;

        foreach (char character in text)
        {
            // A control character in a cell would be a bug upstream, but emitting one
            // here would be a bug the client sees: it would be executed rather than
            // drawn, moving the cursor mid-repaint.
            output.Append(character < ' ' || character == '\u007f' ? ' ' : character);
        }
    }

    // Rendition.

    /// <summary>
    /// Emits the SGR that takes the receiving terminal from <paramref name="from"/> to
    /// <paramref name="to"/>.
    /// <para>
    /// Turning an attribute off is the case worth being careful about. There is no way
    /// to clear a colour back to the terminal's default other than 39 or 49, and no
    /// single code that clears an arbitrary set of flags, so anything subtractive starts
    /// from a reset and rebuilds. Emitting only the additions would let bold or a
    /// background leak into every run after it.
    /// </para>
    /// </summary>
    private static void WriteSgr(StringBuilder output, CellAttributes from, CellAttributes to)
    {
        if (from == to)
        {
            return;
        }

        if (to == CellAttributes.Default)
        {
            output.Append(Esc).Append("[0m");
            return;
        }

        bool needsReset = (from.Flags & ~to.Flags & CellFlags.Rendition) != 0;
        var codes = new List<int>(8);

        if (needsReset)
        {
            codes.Add(0);
            from = CellAttributes.Default;
        }

        AddFlag(codes, from, to, CellFlags.Bold, 1);
        AddFlag(codes, from, to, CellFlags.Dim, 2);
        AddFlag(codes, from, to, CellFlags.Italic, 3);
        AddFlag(codes, from, to, CellFlags.Underline, 4);
        AddFlag(codes, from, to, CellFlags.Blink, 5);
        AddFlag(codes, from, to, CellFlags.Reverse, 7);
        AddFlag(codes, from, to, CellFlags.Hidden, 8);
        AddFlag(codes, from, to, CellFlags.Strikethrough, 9);

        if (from.Foreground != to.Foreground)
        {
            AddColour(codes, to.Foreground, foreground: true);
        }

        if (from.Background != to.Background)
        {
            AddColour(codes, to.Background, foreground: false);
        }

        if (codes.Count == 0)
        {
            return;
        }

        output.Append(Esc).Append('[');

        for (int i = 0; i < codes.Count; i++)
        {
            if (i > 0)
            {
                output.Append(';');
            }

            output.Append(codes[i]);
        }

        output.Append('m');
    }

    private static void AddFlag(
        List<int> codes,
        CellAttributes from,
        CellAttributes to,
        CellFlags flag,
        int code)
    {
        if (to.Has(flag) && !from.Has(flag))
        {
            codes.Add(code);
        }
    }

    /// <summary>
    /// Adds the codes for one colour, in the most compact form that can express it.
    /// <para>
    /// The first sixteen palette entries have dedicated codes, and using them matters
    /// beyond byte count: a terminal theme maps those to its own palette, so a shell
    /// that asked for "red" gets the user's red rather than a fixed one.
    /// </para>
    /// </summary>
    private static void AddColour(List<int> codes, VtColor colour, bool foreground)
    {
        int baseCode = foreground ? 30 : 40;
        int brightBase = foreground ? 90 : 100;

        switch (colour.Kind)
        {
            case VtColorKind.Indexed when colour.Index < 8:
                codes.Add(baseCode + colour.Index);
                break;

            case VtColorKind.Indexed when colour.Index < 16:
                codes.Add(brightBase + (colour.Index - 8));
                break;

            case VtColorKind.Indexed:
                codes.Add(foreground ? 38 : 48);
                codes.Add(5);
                codes.Add(colour.Index);
                break;

            case VtColorKind.Rgb:
                codes.Add(foreground ? 38 : 48);
                codes.Add(2);
                codes.Add(colour.R);
                codes.Add(colour.G);
                codes.Add(colour.B);
                break;

            default:
                codes.Add(foreground ? 39 : 49);
                break;
        }
    }

    // Saved state.

    /// <summary>
    /// Reproduces a saved cursor by putting the terminal into that state and saving it.
    /// <para>
    /// There is no sequence that sets the saved cursor directly, so the only way to
    /// restore one is to briefly become it. Skipping this would be invisible until the
    /// program running in the session restored its cursor and landed somewhere else.
    /// </para>
    /// </summary>
    private static void PlantSavedCursor(
        StringBuilder output,
        SavedCursor saved,
        ref CellAttributes pen,
        bool always)
    {
        if (saved == default && !always)
        {
            return;
        }

        WriteSgr(output, pen, saved.Attributes);
        pen = saved.Attributes;

        WriteCharsets(output, saved.G0, saved.G1, saved.ActiveCharset);

        if (saved.OriginMode)
        {
            output.Append(Esc).Append("[?6h");
        }

        output.Append(Esc).Append('[').Append(saved.Row + 1).Append(';').Append(saved.Column + 1).Append('H');
        output.Append(Esc).Append('7');
    }

    /// <summary>
    /// Undoes whatever <see cref="PlantSavedCursor"/> left behind, so the alternate
    /// screen is painted from power-on state rather than the shell's leftover colours.
    /// </summary>
    private static void ResetPaintingState(StringBuilder output, SavedCursor saved, ref CellAttributes pen)
    {
        if (saved == default)
        {
            return;
        }

        WriteSgr(output, pen, CellAttributes.Default);
        pen = CellAttributes.Default;

        WriteCharsets(output, Charset.Ascii, Charset.Ascii, 0);

        if (saved.OriginMode)
        {
            output.Append(Esc).Append("[?6l");
        }
    }

    private static void WriteCharsets(StringBuilder output, Charset g0, Charset g1, int active)
    {
        output.Append(Esc).Append('(').Append(Designator(g0));
        output.Append(Esc).Append(')').Append(Designator(g1));

        // SI and SO. Emitted unconditionally alongside the designators so that this
        // method always lands the terminal in a known state rather than a relative one.
        output.Append(active == 1 ? '\u000e' : '\u000f');
    }

    private static char Designator(Charset charset) =>
        charset == Charset.DecSpecialGraphics ? '0' : 'B';

    // Trailing state.

    /// <summary>
    /// Restores tab stops, but only when they are not the default every-eight-columns.
    /// <para>
    /// Almost nothing changes them, so the common case emits nothing at all. The ones
    /// that do — full-screen forms, and anything driving a printer-style layout — depend
    /// on them completely, and a tab landing in the wrong column shifts a whole row.
    /// </para>
    /// </summary>
    private static void WriteTabStops(StringBuilder output, TerminalScreen screen)
    {
        if (HasDefaultTabStops(screen))
        {
            return;
        }

        output.Append(Esc).Append("[3g");

        for (int column = 0; column < screen.Columns; column++)
        {
            if (!screen.TabStops[column])
            {
                continue;
            }

            output.Append(Esc).Append('[').Append(column + 1).Append('G');
            output.Append(Esc).Append('H');
        }
    }

    private static bool HasDefaultTabStops(TerminalScreen screen)
    {
        for (int column = 0; column < screen.Columns; column++)
        {
            if (screen.TabStops[column] != (column > 0 && column % 8 == 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Restores the scroll region.
    /// <para>
    /// Emitted before the cursor is placed, because setting a region homes the cursor.
    /// A missing region is not visible on attach at all — it only shows up later, when
    /// the program scrolls and takes the status bar it was protecting with it.
    /// </para>
    /// </summary>
    private static void WriteScrollRegion(StringBuilder output, TerminalScreen screen)
    {
        if (screen.HasFullScrollRegion)
        {
            return;
        }

        output.Append(Esc).Append('[').Append(screen.ScrollTop + 1)
            .Append(';').Append(screen.ScrollBottom + 1).Append('r');
    }

    /// <summary>
    /// Restores the modes that survive a reset only if we say so.
    /// <para>
    /// The input-affecting ones are the reason this exists. A phone attaching to a
    /// program that turned on bracketed paste and application cursor keys would
    /// otherwise send unbracketed text and the wrong arrow-key sequences, and the
    /// program would mis-parse both while the screen looked perfectly correct.
    /// </para>
    /// <para>
    /// Insert mode comes last of the painting-affecting ones because it changes what
    /// printing does; setting it before the repaint would shove each row rightwards.
    /// </para>
    /// </summary>
    private static void WriteModes(StringBuilder output, TerminalScreen screen)
    {
        TerminalModes modes = screen.Modes;

        if (modes.ApplicationCursorKeys)
        {
            output.Append(Esc).Append("[?1h");
        }

        if (modes.ApplicationKeypad)
        {
            output.Append(Esc).Append('=');
        }

        if (!modes.AutoWrap)
        {
            output.Append(Esc).Append("[?7l");
        }

        if (modes.BracketedPaste)
        {
            output.Append(Esc).Append("[?2004h");
        }

        if (modes.MouseClickTracking)
        {
            output.Append(Esc).Append("[?1000h");
        }

        if (modes.MouseDragTracking)
        {
            output.Append(Esc).Append("[?1002h");
        }

        if (modes.MouseMotionTracking)
        {
            output.Append(Esc).Append("[?1003h");
        }

        if (modes.FocusReporting)
        {
            output.Append(Esc).Append("[?1004h");
        }

        if (modes.SgrMouseEncoding)
        {
            output.Append(Esc).Append("[?1006h");
        }

        if (modes.InsertMode)
        {
            output.Append(Esc).Append("[4h");
        }

        // Origin mode homes the cursor, so it has to precede the final placement — and
        // that placement is then relative to the scroll region.
        if (modes.OriginMode)
        {
            output.Append(Esc).Append("[?6h");
        }
    }

    private static void WriteCursorPosition(StringBuilder output, TerminalScreen screen)
    {
        int row = screen.Modes.OriginMode
            ? screen.CursorRow - screen.ScrollTop
            : screen.CursorRow;

        output.Append(Esc).Append('[').Append(Math.Max(1, row + 1))
            .Append(';').Append(screen.CursorColumn + 1).Append('H');
    }

    private static void WriteCursorAppearance(StringBuilder output, TerminalScreen screen)
    {
        if (screen.Modes.CursorStyle != 0)
        {
            output.Append(Esc).Append('[').Append(screen.Modes.CursorStyle).Append(" q");
        }

        if (!screen.Modes.CursorBlink)
        {
            output.Append(Esc).Append("[?12l");
        }

        if (!screen.Modes.CursorVisible)
        {
            output.Append(Esc).Append("[?25l");
        }
    }

    /// <summary>
    /// Restores the window title.
    /// <para>
    /// Terminated with BEL rather than ST because that is what the console host emits
    /// and what every client accepts, and because a lone ESC backslash is one dropped
    /// byte away from swallowing whatever follows it.
    /// </para>
    /// </summary>
    private static void WriteTitle(StringBuilder output, string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        output.Append(Esc).Append("]0;").Append(title).Append('\u0007');
    }
}
