using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace OneRemoteCli.Daemon.Diagnostics;

/// <summary>
/// Builds the agent's logger: a file, and the console when there is one.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentLogging
{
    /// <summary>
    /// Raises the level for a debugging session without a rebuild or a config file.
    /// The agent runs unattended, so the only way to turn logging up is to set this
    /// and restart it.
    /// </summary>
    public const string LevelVariable = "ONEREMOTE_LOG_LEVEL";

    /// <summary>Overrides where the file goes. Used by the tests, and by nobody else.</summary>
    public const string DirectoryVariable = "ONEREMOTE_LOG_DIR";

    /// <summary>
    /// Information, not Debug.
    /// <para>
    /// Debug logs every relayed frame, which on a busy session is thousands of lines a
    /// minute — enough to bury the one line that matters and to make the disk cost
    /// noticeable. The default has to be the level you would want to already have had
    /// when something broke, and that is the lifecycle, not the traffic.
    /// </para>
    /// </summary>
    public const LogLevel DefaultLevel = LogLevel.Information;

    public static ILoggerFactory Create(string? directory = null, bool console = true)
    {
        LogLevel level = ParseLevel(Environment.GetEnvironmentVariable(LevelVariable));

        directory ??= Environment.GetEnvironmentVariable(DirectoryVariable);

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddProvider(new FileLogger(directory));

            if (console)
            {
                builder.AddProvider(new ConsoleLogger());
            }

            // The SignalR client logs a paragraph per reconnect at Information, which
            // buries ours. Its warnings are still worth having.
            builder.AddFilter("Microsoft", LogLevel.Warning);
        });
    }

    /// <summary>
    /// Reads a level name, tolerantly.
    /// <para>
    /// An unrecognised value gives the default rather than an error: somebody who
    /// mistypes this while chasing a bug should get an agent that runs, not one that
    /// refuses to start.
    /// </para>
    /// <para>
    /// The aliases are the words people actually type. Someone turning logging up at
    /// two in the morning writes <c>verbose</c> or <c>warn</c> from muscle memory and
    /// has no reason to know this enum is spelled <c>Debug</c> and <c>Warning</c>;
    /// silently giving them Information instead would send them chasing the wrong
    /// thing entirely.
    /// </para>
    /// </summary>
    public static LogLevel ParseLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultLevel;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "verbose":
                return LogLevel.Debug;
            case "info":
                return LogLevel.Information;
            case "warn":
                return LogLevel.Warning;
            case "off":
            case "silent":
            case "quiet":
                return LogLevel.None;
            default:
                return Enum.TryParse(value, ignoreCase: true, out LogLevel parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : DefaultLevel;
        }
    }
}

/// <summary>
/// The console half.
/// <para>
/// Hand-rolled rather than <c>AddConsole</c> because the agent's console output is
/// also its user interface when run by hand from a terminal, and the framework's
/// two-line-per-entry format is unreadable there.
/// </para>
/// </summary>
internal sealed class ConsoleLogger : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Writer();

    public void Dispose()
    {
    }

    private sealed class Writer : ILogger
    {
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

            string prefix = logLevel >= LogLevel.Warning ? "1remote! " : "1remote: ";

            // Standard error, so that piping the agent's stdout somewhere useful does
            // not mix diagnostics into it.
            Console.Error.WriteLine(prefix + formatter(state, exception));
        }
    }
}
