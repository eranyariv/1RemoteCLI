using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OneRemoteCli.Daemon.Diagnostics;

/// <summary>
/// Where the agent writes what it did.
/// <para>
/// A file, not just the console, because the agent's whole point is to run unattended
/// under a hidden scheduled task: by the time anybody wants to know why their phone
/// stopped seeing a machine, the console it never had is long gone. This is the file
/// the tray's <em>Open logs</em> opens, and the first thing to ask for in a bug report.
/// </para>
/// <para>
/// One file per day, a fortnight kept. Long enough to cover "it broke some time last
/// week", short enough that an agent left running for a year does not quietly fill a
/// disk — which would be a worse fault than the one being diagnosed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileLogger : ILoggerProvider
{
    /// <summary>Beyond this the oldest files go. See the class remarks.</summary>
    public const int DaysKept = 14;

    private readonly string _directory;
    private readonly object _gate = new();

    private string? _path;
    private DateOnly _day;

    public FileLogger(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;

        Directory.CreateDirectory(_directory);
        Prune();
    }

    /// <summary>Beside the rest of the agent's state, so one folder holds everything.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1RemoteCLI",
        "logs");

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>
    /// Appends one line, opening and closing the file each time.
    /// <para>
    /// Deliberately not a held handle: the agent runs for weeks, and a log you cannot
    /// open, copy or delete while it is running is a log nobody ever sends you. The
    /// write rate is a handful of lines a minute, so the open costs nothing that
    /// matters.
    /// </para>
    /// </summary>
    private void Append(string line)
    {
        lock (_gate)
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);

            if (_path is null || _day != today)
            {
                _day = today;
                _path = Path.Combine(_directory, $"agent-{today:yyyy-MM-dd}.log");

                Prune();
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must not stop the thing it is logging.
            }
        }
    }

    private void Prune()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-DaysKept);

            foreach (string file in Directory.EnumerateFiles(_directory, "agent-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth failing over.
        }
    }

    private sealed class Writer(FileLogger owner, string category) : ILogger
    {
        /// <summary>Just the class name: the namespace is identical on every line and only costs width.</summary>
        private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("  ")
                .Append(Abbreviate(logLevel))
                .Append("  ")
                .Append(_category)
                .Append('[')
                .Append(eventId.Id)
                .Append("]  ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                // Indented beneath its line rather than on it, so a stack trace does
                // not make the file unreadable with a grep.
                line.Append(Environment.NewLine)
                    .Append("    ")
                    .Append(exception.ToString().Replace(
                        Environment.NewLine,
                        Environment.NewLine + "    ",
                        StringComparison.Ordinal));
            }

            owner.Append(line.ToString());
        }

        /// <summary>Fixed width, so the columns line up and the file can be read down.</summary>
        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "none",
        };
    }
}
