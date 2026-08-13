using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using OneRemoteCli.Daemon.Pty;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// These exercise the real Windows pseudoconsole. They launch short-lived cmd.exe
/// children rather than mocking, because handle lifetime and EOF behaviour are
/// exactly the parts that cannot be mocked.
/// </summary>
[SupportedOSPlatform("windows")]
public class PseudoConsoleSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StartsAChildAndStreamsItsOutput()
    {
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe /c echo hello-from-conpty",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        Assert.True(session.ProcessId > 0);

        string output = await ReadToEndAsync(session);

        Assert.Contains("hello-from-conpty", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputStreamReachesEndOfFileWhenTheChildExits()
    {
        // The classic ConPTY bug is holding the write end of the output pipe open,
        // which makes this read hang forever instead of completing.
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe /c exit 0",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        Task<string> read = ReadToEndAsync(session);
        Task completed = await Task.WhenAny(read, Task.Delay(Timeout));

        Assert.Same(read, completed);
    }

    [Fact]
    public async Task ReportsTheChildExitCode()
    {
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe /c exit 42",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        await ReadToEndAsync(session);

        int? exitCode = await WaitForExitAsync(session);

        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task ExitCodeIsNullWhileTheChildIsStillRunning()
    {
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        Assert.Null(session.TryGetExitCode());

        await session.WriteAsync("exit\r");
        await ReadToEndAsync(session);
    }

    [Fact]
    public async Task RunsTheChildInTheRequestedWorkingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "1remote-pty-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        try
        {
            await using var session = PseudoConsoleSession.Start(
                "cmd.exe /c cd",
                directory,
                cols: 120,
                rows: 30);

            string output = await ReadToEndAsync(session);

            // Compare the leaf so the 8.3 / long-path form of TEMP does not matter.
            Assert.Contains(Path.GetFileName(directory), output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WritesInputThroughToTheChild()
    {
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe /q",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        await session.WriteAsync("echo round-trip-marker\r");
        await session.WriteAsync("exit\r");

        string output = await ReadToEndAsync(session);

        Assert.Contains("round-trip-marker", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResizeUpdatesTheReportedSize()
    {
        await using var session = PseudoConsoleSession.Start(
            "cmd.exe",
            workingDirectory: null,
            cols: 80,
            rows: 24);

        Assert.Equal(80, session.Cols);
        Assert.Equal(24, session.Rows);

        session.Resize(132, 43);

        Assert.Equal(132, session.Cols);
        Assert.Equal(43, session.Rows);

        await session.WriteAsync("exit\r");
        await ReadToEndAsync(session);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    [InlineData(80, -1)]
    public void RejectsANonPositiveSize(int cols, int rows) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PseudoConsoleSession.Start("cmd.exe /c exit", null, cols, rows));

    [Fact]
    public void SurfacesAFailureToLaunchAMissingProgram() =>
        Assert.ThrowsAny<Exception>(
            () => PseudoConsoleSession.Start(
                "this-program-does-not-exist-1remote.exe",
                null,
                80,
                24));

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var session = PseudoConsoleSession.Start("cmd.exe /c exit 0", null, 80, 24);
        await ReadToEndAsync(session);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => session.Resize(80, 25));
    }

    [Fact]
    public async Task DoesNotLeakHandlesAcrossRepeatedOpenAndClose()
    {
        // Handle leaks are the failure mode that only shows up after a machine has
        // been running for days, so assert on the process handle count directly.
        using var self = Process.GetCurrentProcess();

        for (int i = 0; i < 5; i++)
        {
            await using var warmup = PseudoConsoleSession.Start("cmd.exe /c exit 0", null, 80, 24);
            await ReadToEndAsync(warmup);
        }

        self.Refresh();
        int before = self.HandleCount;

        for (int i = 0; i < 25; i++)
        {
            await using var session = PseudoConsoleSession.Start("cmd.exe /c exit 0", null, 80, 24);
            await ReadToEndAsync(session);
        }

        // Let finalisers and the OS settle before sampling again.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(500);

        self.Refresh();
        int after = self.HandleCount;

        // Each session allocates several handles; leaking them all would show up as
        // roughly 25 * 5. A small drift from unrelated runtime activity is expected.
        Assert.True(
            after - before < 40,
            $"Handle count grew from {before} to {after} over 25 sessions, which suggests a leak.");
    }

    private static async Task<string> ReadToEndAsync(PseudoConsoleSession session)
    {
        var builder = new StringBuilder();
        byte[] buffer = new byte[4096];

        while (true)
        {
            int read = await session.Output.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    private static async Task<int?> WaitForExitAsync(PseudoConsoleSession session)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            int? code = session.TryGetExitCode();
            if (code is not null)
            {
                return code;
            }

            await Task.Delay(25);
        }

        return null;
    }
}
