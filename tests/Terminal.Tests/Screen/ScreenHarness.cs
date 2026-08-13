using System.Text;

using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Screen;

/// <summary>
/// Drives a <see cref="TerminalScreen"/> the way a session does: bytes through the real
/// parser, never by calling the sink methods directly.
/// <para>
/// Going through the parser is not ceremony. A test that called
/// <c>screen.CsiDispatch(...)</c> would be asserting on an interface the session never
/// uses, and would keep passing after a change that broke the byte path — which is the
/// only path that matters.
/// </para>
/// </summary>
internal sealed class ScreenHarness
{
    private readonly VtParser _parser = new();

    public ScreenHarness(int rows = 5, int columns = 20)
    {
        Screen = new TerminalScreen(rows, columns);
    }

    public TerminalScreen Screen { get; }

    public ScreenHarness Feed(string input)
    {
        _parser.Parse(Encoding.UTF8.GetBytes(input), Screen);
        return this;
    }

    /// <summary>Every row, trailing blanks trimmed, as an array for readable assertions.</summary>
    public string[] Lines() =>
        Enumerable.Range(0, Screen.Rows).Select(Screen.GetLine).ToArray();

    public string Line(int row) => Screen.GetLine(row);

    public (int Row, int Column) Cursor => (Screen.CursorRow, Screen.CursorColumn);

    public CellAttributes AttributesAt(int row, int column) => Screen.GetRow(row)[column].Attributes;

    public Cell CellAt(int row, int column) => Screen.GetRow(row)[column];
}
