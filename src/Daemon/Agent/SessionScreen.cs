using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// One session's live screen: every byte the program has written, reduced to what a
/// terminal would be showing right now.
/// <para>
/// This is what makes attaching to something already running work at all. Replaying
/// recent output instead would be right only for a program that appends line by line;
/// anything that addresses the cursor — an editor, a pager, a coding agent's
/// interactive view — would arrive as the tail end of a redraw, which is noise.
/// </para>
/// <para>
/// It costs one screen's worth of cells per session and no history, so a machine with
/// a dozen sessions open pays a few megabytes and never grows from there. That is the
/// whole reason there is no scrollback: an unbounded buffer on the user's own machine
/// would be a memory leak with a feature attached.
/// </para>
/// </summary>
public sealed class SessionScreen
{
    /// <summary>
    /// Guards the parser and the screen together.
    /// <para>
    /// They are one unit: the parser holds half-finished escape sequences and partial
    /// UTF-8 characters between calls, so a second thread feeding it would splice two
    /// streams into one and produce a screen neither of them wrote.
    /// </para>
    /// </summary>
    private readonly object _gate = new();

    private readonly VtParser _parser = new();
    private readonly TerminalScreen _screen;

    public SessionScreen(int cols, int rows)
    {
        _screen = new TerminalScreen(Math.Max(1, rows), Math.Max(1, cols));
    }

    public int Cols
    {
        get
        {
            lock (_gate)
            {
                return _screen.Columns;
            }
        }
    }

    public int Rows
    {
        get
        {
            lock (_gate)
            {
                return _screen.Rows;
            }
        }
    }

    /// <summary>
    /// Applies output from the program, and reports where it could be cut.
    /// <para>
    /// The return value is the offset within <paramref name="bytes"/> of the last
    /// point that is safe to end a network frame at, or -1 if there was none. It comes
    /// from the emulator's own parser because that parser has already looked at every
    /// byte — running a second state machine alongside it to answer the same question
    /// would double the per-byte cost of the hottest path in the system.
    /// </para>
    /// </summary>
    public int Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return -1;
        }

        lock (_gate)
        {
            _parser.Parse(bytes, _screen, out int lastSafeOffset);
            return lastSafeOffset;
        }
    }

    /// <summary>
    /// Reshapes the grid to match the pseudoconsole.
    /// <para>
    /// Kept in step with the real console rather than tracked separately, because a
    /// snapshot taken at the wrong width would tell the phone to render a screen the
    /// program is not drawing.
    /// </para>
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _screen.Resize(rows, cols);
        }
    }

    /// <summary>The current screen as the byte stream that reproduces it.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            return VtSnapshotWriter.Serialize(_screen);
        }
    }

    /// <summary>The screen as plain text. For diagnostics and tests, never for the wire.</summary>
    public string Text()
    {
        lock (_gate)
        {
            return _screen.GetText();
        }
    }

    /// <summary>
    /// Reads the few facts the awaiting-input heuristic needs, in one pass under the
    /// same lock the parser holds.
    /// <para>
    /// Taken together rather than as three separate properties because they only mean
    /// anything as a set: a cursor position read a moment after the visibility flag
    /// could describe a screen that never existed, and this runs on a timer while the
    /// program is free to be writing.
    /// </para>
    /// </summary>
    public ScreenPosture Posture()
    {
        lock (_gate)
        {
            int row = _screen.CursorRow;
            int column = _screen.CursorColumn;

            bool textBefore = false;
            bool textAfter = false;

            if (row >= 0 && row < _screen.Rows)
            {
                ReadOnlySpan<Cell> cells = _screen.GetRow(row);

                for (int x = 0; x < cells.Length; x++)
                {
                    if (cells[x].IsBlank)
                    {
                        continue;
                    }

                    // The cursor's own cell counts as "after": a program that parked it
                    // on top of drawn text is rendering, not asking.
                    if (x < column)
                    {
                        textBefore = true;
                    }
                    else
                    {
                        textAfter = true;
                    }
                }
            }

            return new ScreenPosture(
                _screen.Modes.CursorVisible,
                textBefore && !textAfter,
                LastNonBlankLine());
        }
    }

    /// <summary>Caller must hold the gate.</summary>
    private string LastNonBlankLine()
    {
        for (int row = _screen.Rows - 1; row >= 0; row--)
        {
            string line = _screen.GetLine(row);
            if (line.Trim().Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }
}
