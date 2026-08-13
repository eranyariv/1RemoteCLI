namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// A grid of cells: one screen's worth, with no scrollback.
/// <para>
/// Rows are separate arrays rather than one flat block so that scrolling is a rotation
/// of row references instead of a copy of every cell. A full-screen TUI redrawing at
/// 60 Hz scrolls constantly, and on a 200×50 screen the difference is fifty pointer
/// moves against ten thousand struct copies, per scroll.
/// </para>
/// </summary>
public sealed class ScreenBuffer
{
    private Cell[][] _rows;

    public ScreenBuffer(int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        Rows = rows;
        Columns = columns;
        _rows = new Cell[rows][];

        for (int y = 0; y < rows; y++)
        {
            _rows[y] = NewRow(columns, Cell.Blank);
        }
    }

    public int Rows { get; private set; }

    public int Columns { get; private set; }

    public Cell this[int row, int column]
    {
        get => _rows[row][column];
        set => _rows[row][column] = value;
    }

    /// <summary>A row, for reading. The span is only valid until the next scroll or resize.</summary>
    public ReadOnlySpan<Cell> GetRow(int row) => _rows[row];

    /// <summary>Fills <c>[start, end)</c> of one row.</summary>
    public void Fill(int row, int start, int end, Cell blank)
    {
        Cell[] cells = _rows[row];
        start = Math.Clamp(start, 0, Columns);
        end = Math.Clamp(end, 0, Columns);

        for (int x = start; x < end; x++)
        {
            cells[x] = blank;
        }
    }

    public void FillAll(Cell blank)
    {
        for (int y = 0; y < Rows; y++)
        {
            Fill(y, 0, Columns, blank);
        }
    }

    /// <summary>
    /// Moves the contents of <c>[top, bottom]</c> up by <paramref name="count"/> rows,
    /// blanking what is exposed at the bottom. Rows scrolled off the top are gone —
    /// there is no scrollback, by design.
    /// </summary>
    public void ScrollUp(int top, int bottom, int count, Cell blank)
    {
        int height = bottom - top + 1;

        if (count <= 0 || height <= 0)
        {
            return;
        }

        if (count >= height)
        {
            for (int y = top; y <= bottom; y++)
            {
                Fill(y, 0, Columns, blank);
            }

            return;
        }

        // Rotate: the rows about to be overwritten are reused as the blank rows at the
        // bottom, so scrolling allocates nothing.
        Cell[][] recycled = new Cell[count][];
        Array.Copy(_rows, top, recycled, 0, count);
        Array.Copy(_rows, top + count, _rows, top, height - count);

        for (int i = 0; i < count; i++)
        {
            Cell[] row = recycled[i];
            _rows[bottom - count + 1 + i] = row;
            Array.Fill(row, blank);
        }
    }

    /// <summary>Moves the contents of <c>[top, bottom]</c> down, blanking what is exposed at the top.</summary>
    public void ScrollDown(int top, int bottom, int count, Cell blank)
    {
        int height = bottom - top + 1;

        if (count <= 0 || height <= 0)
        {
            return;
        }

        if (count >= height)
        {
            for (int y = top; y <= bottom; y++)
            {
                Fill(y, 0, Columns, blank);
            }

            return;
        }

        Cell[][] recycled = new Cell[count][];
        Array.Copy(_rows, bottom - count + 1, recycled, 0, count);
        Array.Copy(_rows, top, _rows, top + count, height - count);

        for (int i = 0; i < count; i++)
        {
            Cell[] row = recycled[i];
            _rows[top + i] = row;
            Array.Fill(row, blank);
        }
    }

    /// <summary>Shifts a row right from <paramref name="column"/>, discarding what falls off the end.</summary>
    public void InsertCells(int row, int column, int count, Cell blank)
    {
        Cell[] cells = _rows[row];
        count = Math.Min(count, Columns - column);

        if (count <= 0)
        {
            return;
        }

        Array.Copy(cells, column, cells, column + count, Columns - column - count);
        Array.Fill(cells, blank, column, count);
    }

    /// <summary>Shifts a row left into <paramref name="column"/>, blanking the end.</summary>
    public void DeleteCells(int row, int column, int count, Cell blank)
    {
        Cell[] cells = _rows[row];
        count = Math.Min(count, Columns - column);

        if (count <= 0)
        {
            return;
        }

        Array.Copy(cells, column + count, cells, column, Columns - column - count);
        Array.Fill(cells, blank, Columns - count, count);
    }

    /// <summary>
    /// Resizes the grid, keeping as much content as fits.
    /// </summary>
    /// <param name="cursorRow">
    /// Where the cursor is, which decides what a shrink throws away. Rows are dropped
    /// from the bottom while they are below the cursor, and only then from the top. The
    /// alternative — always dropping from the bottom — erases the prompt on a screen
    /// that is full; always dropping from the top erases the output on a screen that is
    /// nearly empty. Anchoring on the cursor gets both right, and is what conhost does.
    /// </param>
    /// <returns>How many rows were dropped from the top, so the caller can move the cursor with them.</returns>
    public int Resize(int rows, int columns, Cell blank, int cursorRow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        if (rows == Rows && columns == Columns)
        {
            return 0;
        }

        // No reflow. Rewrapping text on resize needs to know where a logical line began,
        // which needs scrollback, which this model deliberately does not have. Programs
        // redraw after a resize anyway, and a wrong reflow looks worse than a clean cut.
        if (columns != Columns)
        {
            for (int y = 0; y < Rows; y++)
            {
                Cell[] resized = NewRow(columns, blank);
                Array.Copy(_rows[y], resized, Math.Min(columns, Columns));
                RepairWideEdge(resized, columns, blank);
                _rows[y] = resized;
            }

            Columns = columns;
        }

        int dropped = 0;

        if (rows != Rows)
        {
            var next = new Cell[rows][];

            if (rows < Rows)
            {
                dropped = Math.Clamp(cursorRow - (rows - 1), 0, Rows - rows);
                Array.Copy(_rows, dropped, next, 0, rows);
            }
            else
            {
                Array.Copy(_rows, next, Rows);

                for (int y = Rows; y < rows; y++)
                {
                    next[y] = NewRow(columns, blank);
                }
            }

            _rows = next;
            Rows = rows;
        }

        return dropped;
    }

    /// <summary>
    /// A truncated row can end with the left half of a wide character whose right half
    /// was cut off. Leaving it would render a character in one column and desynchronise
    /// every column after it.
    /// </summary>
    private static void RepairWideEdge(Cell[] row, int columns, Cell blank)
    {
        if (row[columns - 1].IsWideLeading)
        {
            row[columns - 1] = blank;
        }

        if (row[0].IsWideTrailing)
        {
            row[0] = blank;
        }
    }

    private static Cell[] NewRow(int columns, Cell blank)
    {
        var row = new Cell[columns];
        Array.Fill(row, blank);
        return row;
    }
}
