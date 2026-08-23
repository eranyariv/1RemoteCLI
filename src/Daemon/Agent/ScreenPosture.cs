namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// What the screen looks like it is doing, reduced to the few facts the
/// awaiting-input heuristic needs.
/// <para>
/// This is the whole reason the agent keeps a screen model rather than a byte
/// buffer. Windows offers no way to ask whether a process is blocked on a console
/// read, so "is this waiting for me?" has to be inferred from what is drawn — and a
/// raw stream of bytes cannot answer where the cursor ended up, whether the program
/// hid it, or whether the last thing written was a question or the tail of a
/// progress bar.
/// </para>
/// </summary>
/// <param name="CursorVisible">
/// False when the program hid the cursor, which is what a full-screen renderer does
/// while it is painting and what nothing does while it waits for an answer.
/// </param>
/// <param name="CursorAfterText">
/// True when the cursor sits just past text on its own line with nothing after it:
/// the shape of a prompt. A program that finished a line with a newline leaves the
/// cursor at column zero of a blank row instead, which is what makes this the
/// discriminator between a question and a slow build.
/// </param>
/// <param name="LastLine">
/// The last row with anything on it, trailing blanks trimmed. Used only for the
/// user's own patterns; the heuristic itself never reads the text.
/// </param>
public readonly record struct ScreenPosture(bool CursorVisible, bool CursorAfterText, string LastLine)
{
    /// <summary>A screen nothing is known about, which is never treated as waiting.</summary>
    public static readonly ScreenPosture Unknown = new(false, false, string.Empty);
}

/// <summary>A prompt posture paired with the identity of the rendered screen it came from.</summary>
public readonly record struct AwaitingInputScreen(ScreenPosture Posture, string Fingerprint);
