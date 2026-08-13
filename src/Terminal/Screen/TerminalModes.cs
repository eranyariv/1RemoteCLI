namespace OneRemoteCli.Terminal.Screen;

/// <summary>
/// The DEC private modes that change what the screen looks like or how the client's
/// keyboard should behave.
/// <para>
/// The input-affecting ones — application cursor keys, bracketed paste, mouse reporting
/// — are tracked even though they do not touch a cell, because the snapshot has to
/// restore them. A phone attaching to a program that turned on bracketed paste needs to
/// know that, or the first paste sends unbracketed text and the program mis-parses it.
/// </para>
/// </summary>
public sealed class TerminalModes
{
    /// <summary>DECCKM. Cursor keys send <c>SS3</c> rather than <c>CSI</c>.</summary>
    public bool ApplicationCursorKeys { get; set; }

    /// <summary>DECKPAM. The numeric keypad sends application sequences.</summary>
    public bool ApplicationKeypad { get; set; }

    /// <summary>DECOM. Row addressing is relative to the scroll region.</summary>
    public bool OriginMode { get; set; }

    /// <summary>DECAWM. On by default, which is why ordinary text wraps.</summary>
    public bool AutoWrap { get; set; } = true;

    /// <summary>IRM. Printing shifts the rest of the line right instead of overwriting.</summary>
    public bool InsertMode { get; set; }

    /// <summary>DECTCEM.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Whether the cursor blinks. Purely cosmetic, but part of the snapshot.</summary>
    public bool CursorBlink { get; set; } = true;

    /// <summary>Bracketed paste, mode 2004.</summary>
    public bool BracketedPaste { get; set; }

    /// <summary>Mouse click reporting, mode 1000.</summary>
    public bool MouseClickTracking { get; set; }

    /// <summary>Mouse drag reporting, mode 1002.</summary>
    public bool MouseDragTracking { get; set; }

    /// <summary>Any-motion mouse reporting, mode 1003.</summary>
    public bool MouseMotionTracking { get; set; }

    /// <summary>SGR-encoded mouse coordinates, mode 1006.</summary>
    public bool SgrMouseEncoding { get; set; }

    /// <summary>Focus in/out reporting, mode 1004.</summary>
    public bool FocusReporting { get; set; }

    /// <summary>DECSCUSR. 0 means the terminal's default.</summary>
    public int CursorStyle { get; set; }

    /// <summary>Returns every mode to its power-on value, as <c>RIS</c> does.</summary>
    public void Reset()
    {
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        OriginMode = false;
        AutoWrap = true;
        InsertMode = false;
        CursorVisible = true;
        CursorBlink = true;
        BracketedPaste = false;
        MouseClickTracking = false;
        MouseDragTracking = false;
        MouseMotionTracking = false;
        SgrMouseEncoding = false;
        FocusReporting = false;
        CursorStyle = 0;
    }

    internal TerminalModes Clone() => (TerminalModes)MemberwiseClone();
}
