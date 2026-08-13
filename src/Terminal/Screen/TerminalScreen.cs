using System.Text;

using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// The live screen: what the session currently looks like.
/// <para>
/// This is the session state. A phone attaching mid-session is sent this, not a replay
/// of recent output, because the programs that matter here are cursor-addressed — they
/// emit "move to row 4, erase to end of line, write this" rather than lines of text, and
/// replaying the tail of such a stream into a fresh terminal produces garbage. Modelling
/// the screen instead means an attach is exact regardless of how long the session has
/// been running, and it is what lets a client that has fallen behind be brought back up
/// to date by throwing its backlog away and re-sending the screen.
/// </para>
/// <para>
/// No scrollback, deliberately. Only the visible screen is modelled, which is what keeps
/// a session in the low hundreds of kilobytes and a snapshot inside one cellular round
/// trip. Scrolling back belongs to the desk terminal, which still has it.
/// </para>
/// </summary>
public sealed partial class TerminalScreen : IVtEventSink
{
    private readonly ScreenBuffer _primary;
    private readonly ScreenBuffer _alternate;

    private bool[] _tabStops;
    private SavedCursor _savedPrimary;
    private SavedCursor _savedAlternate;

    /// <summary>
    /// Set after writing to the last column instead of moving the cursor off the screen.
    /// <para>
    /// Wrapping eagerly would be wrong in a way that is easy to miss and painful to
    /// debug: a program that fills the last column of the bottom row and then does
    /// nothing else has not asked the screen to scroll, and scrolling it there would
    /// silently discard the top line. The wrap only happens if another character
    /// actually arrives.
    /// </para>
    /// </summary>
    private bool _pendingWrap;

    public TerminalScreen(int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        _primary = new ScreenBuffer(rows, columns);
        _alternate = new ScreenBuffer(rows, columns);
        _tabStops = DefaultTabStops(columns);
        ScrollBottom = rows - 1;
    }

    public int Rows => _primary.Rows;

    public int Columns => _primary.Columns;

    /// <summary>The buffer being drawn to, which is what a snapshot reproduces.</summary>
    public ScreenBuffer Buffer => IsAlternateScreen ? _alternate : _primary;

    /// <summary>
    /// The primary buffer, whether or not it is the one being drawn to.
    /// <para>
    /// A snapshot needs it even while a full-screen program is running: the shell prompt
    /// underneath is what reappears when that program exits, and the terminal reveals it
    /// rather than asking anyone to redraw it. A snapshot that only carried the
    /// alternate screen would leave a client staring at a blank shell afterwards.
    /// </para>
    /// </summary>
    public ScreenBuffer PrimaryBuffer => _primary;

    /// <summary>The alternate buffer, whether or not it is the one being drawn to.</summary>
    public ScreenBuffer AlternateBuffer => _alternate;

    /// <summary>The cursor the primary screen will get back when the alternate screen is left.</summary>
    public SavedCursor SavedPrimaryCursor => _savedPrimary;

    /// <summary>The cursor saved while the alternate screen is active.</summary>
    public SavedCursor SavedAlternateCursor => _savedAlternate;

    /// <summary>
    /// True while a full-screen program is running. Load-bearing for the snapshot:
    /// restoring the wrong buffer means attaching to an editor shows the shell.
    /// </summary>
    public bool IsAlternateScreen { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public CellAttributes CurrentAttributes { get; private set; } = CellAttributes.Default;

    public TerminalModes Modes { get; } = new();

    /// <summary>Top row of the scroll region, zero-based and inclusive.</summary>
    public int ScrollTop { get; private set; }

    /// <summary>Bottom row of the scroll region, zero-based and inclusive.</summary>
    public int ScrollBottom { get; private set; }

    /// <summary>The window title, from OSC 0 or OSC 2.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Incremented on every BEL, so a caller can decide whether to notify.</summary>
    public int BellCount { get; private set; }

    public Charset G0 { get; private set; } = Charset.Ascii;

    public Charset G1 { get; private set; } = Charset.Ascii;

    /// <summary>Which of <see cref="G0"/> and <see cref="G1"/> is currently mapped to graphic bytes.</summary>
    public int ActiveCharset { get; private set; }

    /// <summary>True when the whole screen is one scroll region, which is the usual case.</summary>
    public bool HasFullScrollRegion => ScrollTop == 0 && ScrollBottom == Rows - 1;

    /// <summary>Columns where a horizontal tab stops.</summary>
    public IReadOnlyList<bool> TabStops => _tabStops;

    /// <summary>The row the cursor is on, for reading.</summary>
    public ReadOnlySpan<Cell> GetRow(int row) => Buffer.GetRow(row);

    /// <summary>The whole screen as plain text, one line per row, trailing blanks trimmed.</summary>
    public string GetText()
    {
        var text = new StringBuilder(Rows * (Columns + 1));

        for (int y = 0; y < Rows; y++)
        {
            text.Append(GetLine(y));

            if (y < Rows - 1)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>One row as plain text, with trailing blanks trimmed.</summary>
    public string GetLine(int row)
    {
        ReadOnlySpan<Cell> cells = Buffer.GetRow(row);
        int end = Columns;

        while (end > 0 && cells[end - 1].IsBlank)
        {
            end--;
        }

        var line = new StringBuilder(end);

        for (int x = 0; x < end; x++)
        {
            if (!cells[x].IsWideTrailing)
            {
                line.Append(cells[x].Text);
            }
        }

        return line.ToString();
    }

    /// <summary>
    /// Changes the screen dimensions, following the pseudoconsole.
    /// <para>
    /// No reflow: text is cut or padded, not rewrapped. Rewrapping needs to know where a
    /// logical line started, which needs scrollback, which this model does not have — and
    /// every program worth resizing redraws itself immediately afterwards anyway.
    /// </para>
    /// </summary>
    public void Resize(int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        if (rows == Rows && columns == Columns)
        {
            return;
        }

        Cell blank = EraseCell;

        int droppedActive = Buffer.Resize(rows, columns, blank, CursorRow);
        ScreenBuffer inactive = IsAlternateScreen ? _primary : _alternate;

        // The inactive buffer has a cursor too — the one that will be restored when the
        // program switches back — so it is anchored on that rather than on the live one.
        int inactiveCursor = (IsAlternateScreen ? _savedPrimary : _savedAlternate).Row;
        inactive.Resize(rows, columns, blank, Math.Clamp(inactiveCursor, 0, inactive.Rows - 1));

        CursorRow = Math.Clamp(CursorRow - droppedActive, 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
        _pendingWrap = false;

        // The old region almost certainly no longer makes sense, and a stale one is
        // worse than none: a region pinned to rows 1-24 on a screen that just grew to 50
        // confines every subsequent scroll to the top half.
        ScrollTop = 0;
        ScrollBottom = rows - 1;

        _tabStops = ResizeTabStops(_tabStops, columns);
        _savedPrimary = ClampSaved(_savedPrimary, rows, columns);
        _savedAlternate = ClampSaved(_savedAlternate, rows, columns);
    }

    /// <summary>Returns the screen to its power-on state, as <c>ESC c</c> does.</summary>
    public void FullReset()
    {
        _primary.FillAll(Cell.Blank);
        _alternate.FillAll(Cell.Blank);

        IsAlternateScreen = false;
        CursorRow = 0;
        CursorColumn = 0;
        CurrentAttributes = CellAttributes.Default;
        Modes.Reset();
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        Title = string.Empty;
        G0 = Charset.Ascii;
        G1 = Charset.Ascii;
        ActiveCharset = 0;
        _pendingWrap = false;
        _tabStops = DefaultTabStops(Columns);
        _savedPrimary = default;
        _savedAlternate = default;
    }

    // Writing.

    /// <inheritdoc />
    public void Print(Rune rune)
    {
        if (ActiveCharsetIs(Charset.DecSpecialGraphics))
        {
            rune = DecSpecialGraphics.Map(rune);
        }

        int width = CharacterWidth.Of(rune);

        if (width == 0)
        {
            AttachCombining(rune);
            return;
        }

        // A screen one column wide cannot hold a double-width character at all. Treating
        // it as narrow keeps the grid consistent; the alternative is to drop the
        // character entirely on a size no real terminal uses.
        if (width == 2 && Columns < 2)
        {
            width = 1;
        }

        WrapIfPending();

        // A double-width character cannot straddle the right edge. Wrapping is what a
        // real terminal does; with autowrap off there is nowhere to put it, and dropping
        // it is better than splitting it across a boundary that does not exist.
        if (width == 2 && CursorColumn == Columns - 1)
        {
            if (!Modes.AutoWrap)
            {
                return;
            }

            Buffer[CursorRow, CursorColumn] = EraseCell;
            CursorColumn = 0;
            Index();
        }

        if (Modes.InsertMode)
        {
            Buffer.InsertCells(CursorRow, CursorColumn, width, EraseCell);
        }

        ClearWideNeighbours(CursorRow, CursorColumn, width);

        CellFlags flags = CurrentAttributes.Flags & CellFlags.Rendition;
        Buffer[CursorRow, CursorColumn] = new Cell(
            rune,
            null,
            CurrentAttributes.With(width == 2 ? flags | CellFlags.WideLeading : flags));

        if (width == 2)
        {
            Buffer[CursorRow, CursorColumn + 1] = new Cell(
                Cell.BlankRune,
                null,
                CurrentAttributes.With(flags | CellFlags.WideTrailing));
        }

        int next = CursorColumn + width;

        if (next >= Columns)
        {
            // Park on the last column and remember that the next character wraps.
            CursorColumn = Columns - 1;
            _pendingWrap = Modes.AutoWrap;
        }
        else
        {
            CursorColumn = next;
        }
    }

    /// <inheritdoc />
    public void Execute(byte control)
    {
        switch (control)
        {
            case 0x07: // BEL
                BellCount++;
                break;

            case 0x08: // BS
                Backspace();
                break;

            case 0x09: // HT
                HorizontalTab(1);
                break;

            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                Index();
                break;

            case 0x0D: // CR
                CursorColumn = 0;
                _pendingWrap = false;
                break;

            case 0x0E: // SO — shift out to G1
                ActiveCharset = 1;
                break;

            case 0x0F: // SI — shift in to G0
                ActiveCharset = 0;
                break;

            default:
                break;
        }
    }

    // Cursor and scrolling primitives.

    /// <summary>Moves down one row, scrolling the region if that would leave it.</summary>
    private void Index()
    {
        if (CursorRow == ScrollBottom)
        {
            Buffer.ScrollUp(ScrollTop, ScrollBottom, 1, EraseCell);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }

        _pendingWrap = false;
    }

    /// <summary>Moves up one row, scrolling the region if that would leave it.</summary>
    private void ReverseIndex()
    {
        if (CursorRow == ScrollTop)
        {
            Buffer.ScrollDown(ScrollTop, ScrollBottom, 1, EraseCell);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }

        _pendingWrap = false;
    }

    private void Backspace()
    {
        _pendingWrap = false;

        if (CursorColumn > 0)
        {
            CursorColumn--;

            // Land on the character, not on its right half, or the next overwrite would
            // leave an orphaned trailing cell.
            if (CursorColumn > 0 && Buffer[CursorRow, CursorColumn].IsWideTrailing)
            {
                CursorColumn--;
            }
        }
    }

    private void HorizontalTab(int count)
    {
        _pendingWrap = false;

        for (int i = 0; i < count; i++)
        {
            int x = CursorColumn + 1;

            while (x < Columns - 1 && !_tabStops[x])
            {
                x++;
            }

            CursorColumn = Math.Min(x, Columns - 1);
        }
    }

    private void BackwardTab(int count)
    {
        _pendingWrap = false;

        for (int i = 0; i < count; i++)
        {
            int x = CursorColumn - 1;

            while (x > 0 && !_tabStops[x])
            {
                x--;
            }

            CursorColumn = Math.Max(x, 0);
        }
    }

    private void WrapIfPending()
    {
        if (!_pendingWrap)
        {
            return;
        }

        _pendingWrap = false;
        CursorColumn = 0;
        Index();
    }

    /// <summary>
    /// Blanks the other half of any wide character that the incoming write would land on
    /// top of. Without this, overwriting the left half of a CJK character leaves its
    /// right half behind as a cell that renders as nothing but still occupies a column.
    /// </summary>
    private void ClearWideNeighbours(int row, int column, int width)
    {
        for (int x = column; x < Math.Min(column + width, Columns); x++)
        {
            Cell cell = Buffer[row, x];

            if (cell.IsWideTrailing && x > 0)
            {
                Buffer[row, x - 1] = EraseCell;
            }
            else if (cell.IsWideLeading && x + 1 < Columns)
            {
                Buffer[row, x + 1] = EraseCell;
            }
        }
    }

    /// <summary>
    /// Attaches a combining mark to the character it modifies, which is the one already
    /// written rather than the one at the cursor.
    /// </summary>
    private void AttachCombining(Rune mark)
    {
        int column = _pendingWrap ? Columns - 1 : CursorColumn - 1;

        if (column < 0)
        {
            // Nothing has been written on this line yet, so there is nothing for the
            // mark to combine with. Dropping it is the only option that does not invent
            // a character.
            return;
        }

        if (Buffer[CursorRow, column].IsWideTrailing && column > 0)
        {
            column--;
        }

        Buffer[CursorRow, column] = Buffer[CursorRow, column].WithCombining(mark);
    }

    private bool ActiveCharsetIs(Charset charset) =>
        (ActiveCharset == 0 ? G0 : G1) == charset;

    /// <summary>A blank in the current background colour, per the background-colour-erase rule.</summary>
    private Cell EraseCell => Cell.BlankWith(CurrentAttributes.Background);

    private static bool[] DefaultTabStops(int columns)
    {
        var stops = new bool[columns];

        for (int x = 8; x < columns; x += 8)
        {
            stops[x] = true;
        }

        return stops;
    }

    /// <summary>
    /// Keeps the stops that still exist and extends the default every-eight pattern into
    /// new columns, rather than starting over. A program that set its own stops and then
    /// widened the window keeps them.
    /// </summary>
    private static bool[] ResizeTabStops(bool[] stops, int columns)
    {
        var next = new bool[columns];
        int shared = Math.Min(stops.Length, columns);
        Array.Copy(stops, next, shared);

        for (int x = shared; x < columns; x++)
        {
            next[x] = x % 8 == 0;
        }

        return next;
    }

    private static SavedCursor ClampSaved(SavedCursor saved, int rows, int columns) => saved with
    {
        Row = Math.Clamp(saved.Row, 0, rows - 1),
        Column = Math.Clamp(saved.Column, 0, columns - 1),
    };
}
