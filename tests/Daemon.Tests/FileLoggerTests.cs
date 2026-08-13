using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OneRemoteCli.Daemon.Diagnostics;
using OneRemoteCli.Protocol.Diagnostics;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The file the agent writes, which is the only account of itself an unattended
/// scheduled task can give.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileLoggerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"1remote-logs-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void WritesToATodayDatedFile()
    {
        using var provider = new FileLogger(_directory);
        provider.CreateLogger("Test").MachineRegistered("machine-7");

        string expected = Path.Combine(_directory, $"agent-{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}.log");

        Assert.True(File.Exists(expected));
        Assert.Contains("machine-7", File.ReadAllText(expected), StringComparison.Ordinal);
    }

    [Fact]
    public void CreatesTheDirectoryRatherThanRequiringIt()
    {
        // The agent's first run after install is on a machine where nothing exists
        // yet, and it must not be the run that fails.
        Assert.False(Directory.Exists(_directory));

        using var provider = new FileLogger(_directory);

        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public void EachLineCarriesTheLevelTheCategoryAndTheEventId()
    {
        // All three are what makes a log greppable. Without the event id you cannot
        // tell two similarly-worded lines apart; without the level you cannot find
        // the failure in a week of chatter.
        using var provider = new FileLogger(_directory);
        provider.CreateLogger("OneRemoteCli.Daemon.Hub.AgentHubClient").HubRefused("account_not_allowed", "Ask an administrator.");

        string line = Directory.EnumerateFiles(_directory).Select(File.ReadAllText).Single();

        Assert.Contains("WARN", line, StringComparison.Ordinal);
        Assert.Contains("AgentHubClient", line, StringComparison.Ordinal);
        Assert.DoesNotContain("OneRemoteCli.Daemon.Hub.AgentHubClient", line, StringComparison.Ordinal);
        Assert.Contains("[1003]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionIsIndentedBeneathItsLineRatherThanOnIt()
    {
        using var provider = new FileLogger(_directory);
        provider.CreateLogger("Test").Failed(new InvalidOperationException("the cause"), "Relaying");

        string written = Directory.EnumerateFiles(_directory).Select(File.ReadAllText).Single();
        string[] lines = written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("Relaying failed.", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("    ", lines[1], StringComparison.Ordinal);
        Assert.Contains("the cause", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgetsFilesOlderThanAFortnightAndKeepsTheRest()
    {
        Directory.CreateDirectory(_directory);

        string ancient = Write("agent-2020-01-01.log", DateTime.Now.AddDays(-FileLogger.DaysKept - 1));
        string recent = Write("agent-2020-01-02.log", DateTime.Now.AddDays(-FileLogger.DaysKept + 1));

        // Not a log file, so not ours to delete: a folder the user has opened is a
        // folder they may have put things in.
        string theirs = Write("notes.txt", DateTime.Now.AddYears(-5));

        using var provider = new FileLogger(_directory);

        Assert.False(File.Exists(ancient));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(theirs));
    }

    [Fact]
    public void ADirectoryItCannotWriteToDoesNotStopTheAgent()
    {
        // The logger must never be the reason the relay stops. A path with an illegal
        // character stands in for the real cases — a locked file, a full disk, a
        // policy-denied folder — which are all the same to the caller.
        var provider = new FileLogger(_directory);

        Directory.Delete(_directory, recursive: true);

        Exception? thrown = Record.Exception(() => provider.CreateLogger("Test").MachineOffline("machine-7"));

        Assert.Null(thrown);

        provider.Dispose();
    }

    [Fact]
    public void TheFileCanBeReadAndDeletedWhileTheAgentIsRunning()
    {
        // The reason it opens and closes per write. A log you cannot send to anyone
        // until you stop the thing that is misbehaving is not much of a log.
        using var provider = new FileLogger(_directory);
        ILogger logger = provider.CreateLogger("Test");

        logger.MachineRegistered("machine-7");

        string path = Directory.EnumerateFiles(_directory).Single();

        Assert.Contains("machine-7", File.ReadAllText(path), StringComparison.Ordinal);

        File.Delete(path);

        logger.MachineOffline("machine-7");

        Assert.Contains("machine-7", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private string Write(string name, DateTime lastWritten)
    {
        string path = Path.Combine(_directory, name);

        File.WriteAllText(path, "old");
        File.SetLastWriteTime(path, lastWritten);

        return path;
    }
}

/// <summary>The knobs an unattended agent can be turned up with when it misbehaves.</summary>
[SupportedOSPlatform("windows")]
public class AgentLoggingTests
{
    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("verbose", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("off", LogLevel.None)]
    [InlineData("none", LogLevel.None)]
    public void UnderstandsWhateverWordSomebodyReachedFor(string configured, LogLevel expected) =>
        Assert.Equal(expected, AgentLogging.ParseLevel(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chatty")]
    public void FallsBackToInformationRatherThanRefusingToStart(string? configured)
    {
        // A typo in an environment variable must not leave the agent silent, and must
        // certainly not stop it starting. Information is the level a bug report needs.
        Assert.Equal(LogLevel.Information, AgentLogging.ParseLevel(configured));
    }

    [Fact]
    public void TurningLoggingOffStillProducesAWorkingFactory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"1remote-logs-{Guid.NewGuid():n}");

        try
        {
            using ILoggerFactory factory = AgentLogging.Create(directory, console: false);

            Exception? thrown = Record.Exception(() => factory.CreateLogger("Test").MachineRegistered("machine-7"));

            Assert.Null(thrown);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
