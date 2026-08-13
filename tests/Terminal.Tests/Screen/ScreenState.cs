using System.Text;
using OneRemoteCli.Terminal.Screen;

namespace OneRemoteCli.Terminal.Tests.Screen;

/// <summary>
/// A canonical, printable description of everything a snapshot is supposed to carry.
/// <para>
/// Comparing screens through a string rather than field by field is deliberate: when a
/// round trip fails, the assertion shows the two descriptions side by side and the
/// difference is readable — "row 7 differs" rather than "expected True, got False".
/// A re-serializer fails in small, plausible ways, so the diff is the whole debugging
/// story.
/// </para>
/// </summary>
internal static class ScreenState
{
    public static string Describe(TerminalScreen screen)
    {
        var text = new StringBuilder();

        text.Append("size ").Append(screen.Rows).Append('x').Append(screen.Columns).Append('\n');
        text.Append("alternate ").Append(screen.IsAlternateScreen).Append('\n');
        text.Append("cursor ").Append(screen.CursorRow).Append(',').Append(screen.CursorColumn).Append('\n');
        text.Append("attributes ").Append(screen.CurrentAttributes).Append('\n');
        text.Append("region ").Append(screen.ScrollTop).Append('-').Append(screen.ScrollBottom).Append('\n');
        text.Append("title ").Append(screen.Title).Append('\n');
        text.Append("charsets ").Append(screen.G0).Append(',').Append(screen.G1)
            .Append(',').Append(screen.ActiveCharset).Append('\n');

        DescribeModes(text, screen.Modes);
        DescribeTabStops(text, screen);
        DescribeSaved(text, "saved-primary", screen.SavedPrimaryCursor);
        DescribeSaved(text, "saved-alternate", screen.SavedAlternateCursor);
        DescribeBuffer(text, "primary", screen.PrimaryBuffer);
        DescribeBuffer(text, "alternate", screen.AlternateBuffer);

        return text.ToString();
    }

    private static void DescribeModes(StringBuilder text, TerminalModes modes)
    {
        text.Append("modes")
            .Append(" appcursor=").Append(modes.ApplicationCursorKeys)
            .Append(" appkeypad=").Append(modes.ApplicationKeypad)
            .Append(" origin=").Append(modes.OriginMode)
            .Append(" wrap=").Append(modes.AutoWrap)
            .Append(" insert=").Append(modes.InsertMode)
            .Append(" visible=").Append(modes.CursorVisible)
            .Append(" blink=").Append(modes.CursorBlink)
            .Append(" paste=").Append(modes.BracketedPaste)
            .Append(" mouse=").Append(modes.MouseClickTracking)
            .Append(',').Append(modes.MouseDragTracking)
            .Append(',').Append(modes.MouseMotionTracking)
            .Append(',').Append(modes.SgrMouseEncoding)
            .Append(" focus=").Append(modes.FocusReporting)
            .Append(" style=").Append(modes.CursorStyle)
            .Append('\n');
    }

    private static void DescribeTabStops(StringBuilder text, TerminalScreen screen)
    {
        text.Append("tabs");

        for (int column = 0; column < screen.Columns; column++)
        {
            if (screen.TabStops[column])
            {
                text.Append(' ').Append(column);
            }
        }

        text.Append('\n');
    }

    private static void DescribeSaved(StringBuilder text, string label, SavedCursor saved)
    {
        text.Append(label).Append(' ').Append(saved.Row).Append(',').Append(saved.Column)
            .Append(' ').Append(saved.Attributes)
            .Append(" origin=").Append(saved.OriginMode)
            .Append(" charsets=").Append(saved.G0).Append(',').Append(saved.G1)
            .Append(',').Append(saved.ActiveCharset)
            .Append('\n');
    }

    private static void DescribeBuffer(StringBuilder text, string label, ScreenBuffer buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            ReadOnlySpan<Cell> cells = buffer.GetRow(row);
            var line = new StringBuilder();

            for (int column = 0; column < cells.Length; column++)
            {
                Cell cell = cells[column];

                // Only cells that carry something are described, so a mostly-blank row
                // produces a short line and a real difference stands out.
                if (cell.IsBlank && cell.Attributes == CellAttributes.Default)
                {
                    continue;
                }

                line.Append(column).Append(':')
                    .Append(Escape(cell.Text)).Append('|')
                    .Append(cell.Attributes).Append(' ');
            }

            if (line.Length > 0)
            {
                text.Append(label).Append('[').Append(row).Append("] ").Append(line).Append('\n');
            }
        }
    }

    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            if (character is < ' ' or > '~')
            {
                escaped.Append("\\u").Append(((int)character).ToString("x4"));
                continue;
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
