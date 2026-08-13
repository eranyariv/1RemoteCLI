using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// A log sink that keeps everything, so a test can assert on what was never written.
/// <para>
/// Records the formatted message, the exception, and every structured value
/// separately. Asserting only on the formatted message would miss the likelier
/// mistake: a payload passed as a structured field, which a console renderer folds
/// into the text but a JSON sink writes out in full.
/// </para>
/// </summary>
public sealed class RecordingLogger : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _records = [];

    /// <summary>Everything any component logged, formatted and structured alike.</summary>
    public IReadOnlyList<string> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>Every record, as one string, for a single containment assertion.</summary>
    public string All() => string.Join('\n', _records);

    private sealed class Writer(RecordingLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <summary>
        /// Always. A redaction test that ran at Information would prove nothing about
        /// the Debug and Trace levels, which is exactly where a payload log would be
        /// added and where it would sit unnoticed until someone turned logging up.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            owner._records.Enqueue($"{category}[{eventId.Id}] {logLevel}: {formatter(state, exception)}");

            if (exception is not null)
            {
                owner._records.Enqueue(exception.ToString());
            }

            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                {
                    owner._records.Enqueue($"  {value.Key}={value.Value}");
                }
            }
        }
    }
}
