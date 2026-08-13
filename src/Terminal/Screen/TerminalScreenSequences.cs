using System.Text;

using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// The escape sequences <see cref="TerminalScreen"/> acts on.
/// <para>
/// Split into its own file because it is a long, flat table of cases and mixing it in
/// with the screen's state and invariants makes both harder to read. The rule for what
/// is here is coverage of what these CLIs actually emit — a shell prompt, a coding
/// agent's redraw loop, a full-screen TUI — rather than all of DEC's catalogue.
/// </para>
/// <para>
/// Sequences that ask the terminal a question (device attributes, cursor position
/// reports) are deliberately ignored. This emulator is an observer on a stream the real
/// console has already answered; replying would mean writing to the session's input,
/// which would inject bytes the program never asked for.
/// </para>
/// </summary>
public sealed partial class TerminalScreen
{
    /// <inheritdoc />
    public void EscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        if (intermediates.Length == 1)
        {
            switch (intermediates[0])
            {
                case (byte)'(':
                    G0 = CharsetFor(final);
                    return;

                case (byte)')':
                case (byte)'*':
                case (byte)'+':
                    G1 = CharsetFor(final);
                    return;

                case (byte)'#' when final == (byte)'8':
                    ScreenAlignmentTest();
                    return;

                default:
                    return;
            }
        }

        if (!intermediates.IsEmpty)
        {
            return;
        }

        switch (final)
        {
            case (byte)'D': // IND
                Index();
                break;

            case (byte)'E': // NEL
                CursorColumn = 0;
                Index();
                break;

            case (byte)'M': // RI
                ReverseIndex();
                break;

            case (byte)'H': // HTS
                if (CursorColumn < Columns)
                {
                    _tabStops[CursorColumn] = true;
                }

                break;

            case (byte)'7': // DECSC
                SaveCursor();
                break;

            case (byte)'8': // DECRC
                RestoreCursor();
                break;

            case (byte)'c': // RIS
                FullReset();
                break;

            case (byte)'=': // DECKPAM
                Modes.ApplicationKeypad = true;
                break;

            case (byte)'>': // DECKPNM
                Modes.ApplicationKeypad = false;
                break;

            default:
                break;
        }
    }

    /// <inheritdoc />
    public void CsiDispatch(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        if (intermediates.Length == 1 && intermediates[0] == (byte)'?')
        {
            PrivateMode(in parameters, final);
            return;
        }

        // The space intermediate carries the cursor-style sequence, which is the only
        // intermediate-bearing CSI these programs emit.
        if (intermediates.Length == 1 && intermediates[0] == (byte)' ' && final == (byte)'q')
        {
            Modes.CursorStyle = parameters.Get(0, 0);
            return;
        }

        if (!intermediates.IsEmpty)
        {
            return;
        }

        switch (final)
        {
            case (byte)'@': // ICH
                Buffer.InsertCells(CursorRow, CursorColumn, parameters.Get(0, 1), EraseCell);
                break;

            case (byte)'A': // CUU
                MoveCursorRow(-parameters.Get(0, 1));
                break;

            case (byte)'B': // CUD
            case (byte)'e': // VPR
                MoveCursorRow(parameters.Get(0, 1));
                break;

            case (byte)'C': // CUF
            case (byte)'a': // HPR
                MoveCursorColumn(parameters.Get(0, 1));
                break;

            case (byte)'D': // CUB
                MoveCursorColumn(-parameters.Get(0, 1));
                break;

            case (byte)'E': // CNL
                MoveCursorRow(parameters.Get(0, 1));
                CursorColumn = 0;
                break;

            case (byte)'F': // CPL
                MoveCursorRow(-parameters.Get(0, 1));
                CursorColumn = 0;
                break;

            case (byte)'G': // CHA
            case (byte)'`': // HPA
                SetCursorColumn(parameters.Get(0, 1) - 1);
                break;

            case (byte)'H': // CUP
            case (byte)'f': // HVP
                SetCursorPosition(parameters.Get(0, 1) - 1, parameters.Get(1, 1) - 1);
                break;

            case (byte)'I': // CHT
                HorizontalTab(parameters.Get(0, 1));
                break;

            case (byte)'J': // ED
                EraseInDisplay(parameters.Get(0, 0));
                break;

            case (byte)'K': // EL
                EraseInLine(parameters.Get(0, 0));
                break;

            case (byte)'L': // IL
                InsertLines(parameters.Get(0, 1));
                break;

            case (byte)'M': // DL
                DeleteLines(parameters.Get(0, 1));
                break;

            case (byte)'P': // DCH
                Buffer.DeleteCells(CursorRow, CursorColumn, parameters.Get(0, 1), EraseCell);
                break;

            case (byte)'S': // SU
                Buffer.ScrollUp(ScrollTop, ScrollBottom, parameters.Get(0, 1), EraseCell);
                break;

            case (byte)'T': // SD
                Buffer.ScrollDown(ScrollTop, ScrollBottom, parameters.Get(0, 1), EraseCell);
                break;

            case (byte)'X': // ECH
                EraseCharacters(parameters.Get(0, 1));
                break;

            case (byte)'Z': // CBT
                BackwardTab(parameters.Get(0, 1));
                break;

            case (byte)'d': // VPA
                SetCursorRow(parameters.Get(0, 1) - 1);
                break;

            case (byte)'g': // TBC
                ClearTabStops(parameters.Get(0, 0));
                break;

            case (byte)'h': // SM
                SetAnsiMode(in parameters, true);
                break;

            case (byte)'l': // RM
                SetAnsiMode(in parameters, false);
                break;

            case (byte)'m': // SGR
                SelectGraphicRendition(in parameters);
                break;

            case (byte)'r': // DECSTBM
                SetScrollRegion(parameters.Get(0, 1) - 1, parameters.Get(1, Rows) - 1);
                break;

            case (byte)'s': // ANSI.SYS save cursor
                SaveCursor();
                break;

            case (byte)'u': // ANSI.SYS restore cursor
                RestoreCursor();
                break;

            default:
                // Device attributes, status reports, window operations: all questions
                // the real console already answered, or requests this model has no
                // window to act on.
                break;
        }
    }

    /// <inheritdoc />
    public void OscDispatch(ReadOnlySpan<byte> data)
    {
        int separator = data.IndexOf((byte)';');

        if (separator < 0)
        {
            return;
        }

        ReadOnlySpan<byte> command = data[..separator];
        ReadOnlySpan<byte> payload = data[(separator + 1)..];

        // 0 sets icon name and title together; 2 sets the title alone. Both are what a
        // shell uses to advertise the running command, which is the one piece of OSC
        // this system shows the user.
        if (command.SequenceEqual("0"u8) || command.SequenceEqual("2"u8))
        {
            Title = DecodeTitle(payload);
        }
    }

    /// <inheritdoc />
    public void Hook(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        // Device control strings carry things this model has no use for: sixel images,
        // terminfo queries, DECRQSS replies. The payload is consumed and dropped rather
        // than being allowed to fall through to Print, which would paint it on screen.
    }

    /// <inheritdoc />
    public void Put(byte data)
    {
    }

    /// <inheritdoc />
    public void Unhook()
    {
    }

    // Cursor movement.

    private void MoveCursorRow(int delta)
    {
        _pendingWrap = false;

        // Movement stops at the scroll region rather than passing through it, but only
        // if the cursor started inside it — a cursor parked below the region can still
        // move freely down there.
        int low = CursorRow >= ScrollTop ? ScrollTop : 0;
        int high = CursorRow <= ScrollBottom ? ScrollBottom : Rows - 1;

        CursorRow = Math.Clamp(CursorRow + delta, low, high);
    }

    private void MoveCursorColumn(int delta)
    {
        _pendingWrap = false;
        CursorColumn = Math.Clamp(CursorColumn + delta, 0, Columns - 1);
    }

    private void SetCursorColumn(int column)
    {
        _pendingWrap = false;
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    private void SetCursorRow(int row)
    {
        _pendingWrap = false;

        if (Modes.OriginMode)
        {
            CursorRow = Math.Clamp(ScrollTop + row, ScrollTop, ScrollBottom);
            return;
        }

        CursorRow = Math.Clamp(row, 0, Rows - 1);
    }

    private void SetCursorPosition(int row, int column)
    {
        SetCursorRow(row);
        SetCursorColumn(column);
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor(
            CursorRow,
            CursorColumn,
            CurrentAttributes,
            Modes.OriginMode,
            G0,
            G1,
            ActiveCharset);

        if (IsAlternateScreen)
        {
            _savedAlternate = saved;
        }
        else
        {
            _savedPrimary = saved;
        }
    }

    private void RestoreCursor()
    {
        SavedCursor saved = IsAlternateScreen ? _savedAlternate : _savedPrimary;

        CurrentAttributes = saved.Attributes;
        Modes.OriginMode = saved.OriginMode;
        G0 = saved.G0;
        G1 = saved.G1;
        ActiveCharset = saved.ActiveCharset;
        CursorRow = Math.Clamp(saved.Row, 0, Rows - 1);
        CursorColumn = Math.Clamp(saved.Column, 0, Columns - 1);
        _pendingWrap = false;
    }

    // Erasing and editing.

    private void EraseInDisplay(int mode)
    {
        Cell blank = EraseCell;

        switch (mode)
        {
            case 0: // cursor to end
                Buffer.Fill(CursorRow, CursorColumn, Columns, blank);

                for (int y = CursorRow + 1; y < Rows; y++)
                {
                    Buffer.Fill(y, 0, Columns, blank);
                }

                break;

            case 1: // start to cursor, inclusive
                for (int y = 0; y < CursorRow; y++)
                {
                    Buffer.Fill(y, 0, Columns, blank);
                }

                Buffer.Fill(CursorRow, 0, CursorColumn + 1, blank);
                break;

            case 2: // the whole screen; the cursor does not move
            case 3: // and the scrollback, which this model does not have
                Buffer.FillAll(blank);
                break;

            default:
                return;
        }

        _pendingWrap = false;
    }

    private void EraseInLine(int mode)
    {
        Cell blank = EraseCell;

        switch (mode)
        {
            case 0:
                Buffer.Fill(CursorRow, CursorColumn, Columns, blank);
                break;

            case 1:
                Buffer.Fill(CursorRow, 0, CursorColumn + 1, blank);
                break;

            case 2:
                Buffer.Fill(CursorRow, 0, Columns, blank);
                break;

            default:
                return;
        }

        _pendingWrap = false;
    }

    private void EraseCharacters(int count)
    {
        Buffer.Fill(CursorRow, CursorColumn, CursorColumn + count, EraseCell);
        _pendingWrap = false;
    }

    private void InsertLines(int count)
    {
        // Line insertion is defined only inside the scroll region. Outside it the
        // sequence does nothing at all, which programs rely on to leave a status bar
        // untouched.
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        Buffer.ScrollDown(CursorRow, ScrollBottom, count, EraseCell);
        CursorColumn = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        Buffer.ScrollUp(CursorRow, ScrollBottom, count, EraseCell);
        CursorColumn = 0;
        _pendingWrap = false;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);

        // A region needs at least two lines to scroll. An inverted or single-line one is
        // a malformed request, and the defined response is to leave the region alone.
        if (top >= bottom)
        {
            return;
        }

        ScrollTop = top;
        ScrollBottom = bottom;

        // Setting the region homes the cursor, which is what makes the common
        // "set region then draw from the top" idiom work without an explicit CUP.
        CursorRow = Modes.OriginMode ? ScrollTop : 0;
        CursorColumn = 0;
        _pendingWrap = false;
    }

    private void ClearTabStops(int mode)
    {
        switch (mode)
        {
            case 0 when CursorColumn < Columns:
                _tabStops[CursorColumn] = false;
                break;

            case 3:
                Array.Clear(_tabStops);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Fills the screen with <c>E</c>. A DEC diagnostic that survives because terminal
    /// test suites use it to check grid dimensions, and it costs nothing to support.
    /// </summary>
    private void ScreenAlignmentTest()
    {
        var e = new Cell(new Rune('E'), null, CellAttributes.Default);
        Buffer.FillAll(e);

        CursorRow = 0;
        CursorColumn = 0;
        _pendingWrap = false;
    }

    // Modes.

    private void SetAnsiMode(scoped in VtParams parameters, bool enabled)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] == 4)
            {
                Modes.InsertMode = enabled;
            }
        }
    }

    private void PrivateMode(scoped in VtParams parameters, byte final)
    {
        if (final is not ((byte)'h' or (byte)'l'))
        {
            return;
        }

        bool enabled = final == (byte)'h';

        for (int i = 0; i < parameters.Count; i++)
        {
            switch (parameters[i])
            {
                case 1:
                    Modes.ApplicationCursorKeys = enabled;
                    break;

                case 6:
                    Modes.OriginMode = enabled;

                    // Origin mode redefines what row 1 means, so the cursor is homed to
                    // whichever origin is now in force.
                    CursorRow = enabled ? ScrollTop : 0;
                    CursorColumn = 0;
                    _pendingWrap = false;
                    break;

                case 7:
                    Modes.AutoWrap = enabled;
                    _pendingWrap = false;
                    break;

                case 12:
                    Modes.CursorBlink = enabled;
                    break;

                case 25:
                    Modes.CursorVisible = enabled;
                    break;

                case 47:
                    SwitchScreen(enabled, clear: false, saveCursor: false);
                    break;

                case 1000:
                    Modes.MouseClickTracking = enabled;
                    break;

                case 1002:
                    Modes.MouseDragTracking = enabled;
                    break;

                case 1003:
                    Modes.MouseMotionTracking = enabled;
                    break;

                case 1004:
                    Modes.FocusReporting = enabled;
                    break;

                case 1006:
                    Modes.SgrMouseEncoding = enabled;
                    break;

                case 1047:
                    SwitchScreen(enabled, clear: true, saveCursor: false);
                    break;

                case 1048:
                    if (enabled)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }

                    break;

                case 1049:
                    SwitchScreen(enabled, clear: true, saveCursor: true);
                    break;

                case 2004:
                    Modes.BracketedPaste = enabled;
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Switches between the primary and alternate screens.
    /// <para>
    /// Getting this wrong is the single most visible way the model can fail: every
    /// full-screen program — an editor, a pager, a coding agent's interactive view —
    /// runs on the alternate screen, so a mismatch means attaching to one of them shows
    /// the shell that launched it. The cursor is saved and restored around the switch
    /// because that is what makes the shell prompt reappear exactly where it was when
    /// the program exits.
    /// </para>
    /// </summary>
    private void SwitchScreen(bool toAlternate, bool clear, bool saveCursor)
    {
        if (toAlternate == IsAlternateScreen)
        {
            return;
        }

        if (toAlternate)
        {
            if (saveCursor)
            {
                SaveCursor();
            }

            IsAlternateScreen = true;

            if (clear)
            {
                _alternate.FillAll(EraseCell);
            }

            // The alternate screen has no scroll region of its own to inherit.
            ScrollTop = 0;
            ScrollBottom = Rows - 1;
            _pendingWrap = false;
            return;
        }

        if (clear)
        {
            _alternate.FillAll(EraseCell);
        }

        IsAlternateScreen = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        _pendingWrap = false;

        if (saveCursor)
        {
            RestoreCursor();
        }
    }

    // Rendition.

    private void SelectGraphicRendition(scoped in VtParams parameters)
    {
        if (parameters.Count == 0)
        {
            CurrentAttributes = CellAttributes.Default;
            return;
        }

        CellAttributes attributes = CurrentAttributes;

        for (int i = 0; i < parameters.Count; i++)
        {
            ReadOnlySpan<int> group = parameters.SubParams(i);
            int code = group[0];

            switch (code)
            {
                case 0:
                    attributes = CellAttributes.Default;
                    break;

                case 1:
                    attributes = attributes.Add(CellFlags.Bold);
                    break;

                case 2:
                    attributes = attributes.Add(CellFlags.Dim);
                    break;

                case 3:
                    attributes = attributes.Add(CellFlags.Italic);
                    break;

                case 4:
                    // 4:0 turns underlining off; every other style variant is still an
                    // underline as far as this model is concerned.
                    attributes = group.Length > 1 && group[1] == 0
                        ? attributes.Remove(CellFlags.Underline)
                        : attributes.Add(CellFlags.Underline);
                    break;

                case 5:
                case 6:
                    attributes = attributes.Add(CellFlags.Blink);
                    break;

                case 7:
                    attributes = attributes.Add(CellFlags.Reverse);
                    break;

                case 8:
                    attributes = attributes.Add(CellFlags.Hidden);
                    break;

                case 9:
                    attributes = attributes.Add(CellFlags.Strikethrough);
                    break;

                case 21:
                case 24:
                    attributes = attributes.Remove(CellFlags.Underline);
                    break;

                case 22:
                    attributes = attributes.Remove(CellFlags.Bold | CellFlags.Dim);
                    break;

                case 23:
                    attributes = attributes.Remove(CellFlags.Italic);
                    break;

                case 25:
                    attributes = attributes.Remove(CellFlags.Blink);
                    break;

                case 27:
                    attributes = attributes.Remove(CellFlags.Reverse);
                    break;

                case 28:
                    attributes = attributes.Remove(CellFlags.Hidden);
                    break;

                case 29:
                    attributes = attributes.Remove(CellFlags.Strikethrough);
                    break;

                case >= 30 and <= 37:
                    attributes = attributes.WithForeground(VtColor.FromIndex((byte)(code - 30)));
                    break;

                case 38:
                    attributes = attributes.WithForeground(ExtendedColour(in parameters, group, ref i));
                    break;

                case 39:
                    attributes = attributes.WithForeground(VtColor.Default);
                    break;

                case >= 40 and <= 47:
                    attributes = attributes.WithBackground(VtColor.FromIndex((byte)(code - 40)));
                    break;

                case 48:
                    attributes = attributes.WithBackground(ExtendedColour(in parameters, group, ref i));
                    break;

                case 49:
                    attributes = attributes.WithBackground(VtColor.Default);
                    break;

                case 58:
                    // Underline colour. Parsed so its arguments are consumed rather than
                    // being mistaken for further attributes, then discarded.
                    ExtendedColour(in parameters, group, ref i);
                    break;

                case >= 90 and <= 97:
                    attributes = attributes.WithForeground(VtColor.FromIndex((byte)(code - 90 + 8)));
                    break;

                case >= 100 and <= 107:
                    attributes = attributes.WithBackground(VtColor.FromIndex((byte)(code - 100 + 8)));
                    break;

                default:
                    break;
            }
        }

        CurrentAttributes = attributes;
    }

    /// <summary>
    /// Reads the arguments of <c>38</c>, <c>48</c> or <c>58</c>, which may be written
    /// either as sub-parameters of this one or as the parameters that follow it.
    /// <para>
    /// Both spellings are in the wild. The colon form is correct and unambiguous; the
    /// semicolon form is what almost everything actually emits, and it is ambiguous
    /// precisely because its arguments are indistinguishable from further attributes —
    /// which is why the index has to be advanced past them here.
    /// </para>
    /// </summary>
    private static VtColor ExtendedColour(
        scoped in VtParams parameters,
        ReadOnlySpan<int> group,
        ref int index)
    {
        if (group.Length > 1)
        {
            return ColourFromSubParams(group);
        }

        int kind = parameters.Count > index + 1 ? parameters[index + 1] : -1;

        switch (kind)
        {
            case 5 when parameters.Count > index + 2:
                index += 2;
                return VtColor.FromIndex((byte)Math.Clamp(parameters[index], 0, 255));

            case 2 when parameters.Count > index + 4:
                byte r = Component(parameters[index + 2]);
                byte g = Component(parameters[index + 3]);
                byte b = Component(parameters[index + 4]);
                index += 4;
                return VtColor.FromRgb(r, g, b);

            default:
                // Malformed. Consuming the selector but not inventing arguments leaves
                // the rest of the sequence to be read as attributes, which is the least
                // surprising reading of a truncated colour.
                if (kind >= 0)
                {
                    index++;
                }

                return VtColor.Default;
        }
    }

    private static VtColor ColourFromSubParams(ReadOnlySpan<int> group) => group[1] switch
    {
        5 when group.Length > 2 => VtColor.FromIndex((byte)Math.Clamp(group[2], 0, 255)),

        // The full form carries a colour space identifier before the components, which
        // nothing uses and which is conventionally left empty — hence both lengths.
        2 when group.Length > 5 => VtColor.FromRgb(
            Component(group[3]),
            Component(group[4]),
            Component(group[5])),
        2 when group.Length > 4 => VtColor.FromRgb(
            Component(group[2]),
            Component(group[3]),
            Component(group[4])),
        _ => VtColor.Default,
    };

    private static byte Component(int value) => (byte)Math.Clamp(value, 0, 255);

    private static Charset CharsetFor(byte designator) =>
        designator == (byte)'0' ? Charset.DecSpecialGraphics : Charset.Ascii;

    private static string DecodeTitle(ReadOnlySpan<byte> payload)
    {
        // A title is one line by definition, and a stream that puts controls in one is
        // either broken or trying to smuggle escape sequences through a field that gets
        // displayed. Neither should reach a UI.
        const int MaxTitleLength = 512;

        Span<byte> filtered = payload.Length <= 512 ? stackalloc byte[payload.Length] : new byte[payload.Length];
        int length = 0;

        foreach (byte b in payload)
        {
            if (b >= 0x20 && b != 0x7F && length < MaxTitleLength)
            {
                filtered[length++] = b;
            }
        }

        return Encoding.UTF8.GetString(filtered[..length]);
    }
}
