using System.Text;
using OneRemoteCli.Daemon.Pty;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Whether a resize reaches the <em>program</em>, as opposed to reaching the handle.
/// <para>
/// <see cref="PseudoConsoleSessionTests"/> already checks that <c>Resize</c> updates the
/// session's own idea of its size, and the end-to-end test checks that a phone's resize
/// travels four hops and lands on that call. Neither asks the only question that
/// matters to the user: does the thing running inside the pseudoconsole now believe it
/// has a different amount of room? A full-screen program that was not told will keep
/// drawing its interface off the edge of the phone's screen, and every test in the tree
/// would still be green.
/// </para>
/// </summary>
public sealed class PseudoConsoleResizeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheHostedProgramSeesTheNewWidth()
    {
        await using PseudoConsoleSession session = PseudoConsoleSession.Start(
            "cmd.exe /q",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        var output = new StringBuilder();
        Task reading = ReadInto(session, output);

        // `mode con` is the one thing every Windows console has that will state its own
        // dimensions, which makes it a probe with no dependencies of its own.
        await session.WriteAsync("mode con\r");
        await WaitFor(output, "Columns", 1);

        session.Resize(120, 40);

        await session.WriteAsync("mode con\r");
        await WaitFor(output, "Columns", 2);

        await session.WriteAsync("exit\r");
        await session.Exited.WaitAsync(Patience);
        await reading.WaitAsync(Patience);

        string[] widths = output.ToString()
            .Split('\n')
            .Where(line => line.Contains("Columns", StringComparison.Ordinal))
            .Select(line => line.Split(':').Last().Trim())
            .ToArray();

        // The console redraws itself when it is resized, so the same report can appear
        // more than once; what matters is that the first word on the subject was the old
        // width and the last is the new one.
        Assert.True(widths.Length >= 2, $"Expected at least two width reports, saw: {string.Join(" | ", widths)}");
        Assert.Equal("80", widths[0]);
        Assert.Equal("120", widths[^1]);
    }

    private static async Task WaitFor(StringBuilder output, string text, int occurrences)
    {
        DateTime deadline = DateTime.UtcNow.Add(Patience);

        while (DateTime.UtcNow < deadline)
        {
            int found = 0;
            int at = 0;
            string snapshot = Snapshot(output);

            while ((at = snapshot.IndexOf(text, at, StringComparison.Ordinal)) >= 0)
            {
                found++;
                at += text.Length;
            }

            if (found >= occurrences)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Saw fewer than {occurrences} of '{text}'. Output was:\n{Snapshot(output)}");
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static async Task ReadInto(PseudoConsoleSession session, StringBuilder output)
    {
        var buffer = new byte[4096];

        while (true)
        {
            int read = await session.Output.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
    }
}
