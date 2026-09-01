using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>Reads the task database Copilot Desktop keeps beside each persisted session.</summary>
internal sealed class CopilotTaskPlanIndex(Action<string>? log = null, string? sessionStateRoot = null)
{
    private readonly string _sessionStateRoot = sessionStateRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot",
        "session-state");
    private readonly Action<string>? _log = log;
    private readonly ConcurrentDictionary<string, byte> _reportedFailures = new(StringComparer.Ordinal);

    public async Task<CopilotTaskPlanRead> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out _))
        {
            return CopilotTaskPlanRead.Unavailable;
        }

        string path = Path.Combine(_sessionStateRoot, sessionId, "session.db");
        if (!File.Exists(path))
        {
            return CopilotTaskPlanRead.Unavailable;
        }

        bool succeeded = false;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction();

            HashSet<string> todoColumns =
                await ColumnsAsync(connection, transaction, "todos", cancellationToken)
                    .ConfigureAwait(false);
            if (!todoColumns.IsSupersetOf(["id", "title", "status"]))
            {
                succeeded = true;
                return CopilotTaskPlanRead.Unavailable;
            }

            var tasks = new List<ChatTaskEntry>();
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    SELECT id, title, status
                    FROM todos
                    ORDER BY rowid
                    """;
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                    string title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    if (id.Length == 0 || title.Length == 0)
                    {
                        continue;
                    }

                    tasks.Add(new ChatTaskEntry
                    {
                        TaskId = id,
                        Title = title,
                        Status = NormalizeStatus(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    });
                }
            }

            if (tasks.Count == 0)
            {
                succeeded = true;
                return CopilotTaskPlanRead.Unavailable;
            }

            HashSet<string> dependencyColumns =
                await ColumnsAsync(connection, transaction, "todo_deps", cancellationToken)
                    .ConfigureAwait(false);
            if (dependencyColumns.IsSupersetOf(["todo_id", "depends_on"]))
            {
                Dictionary<string, ChatTaskEntry> byId =
                    tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
                var dependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    SELECT todo_id, depends_on
                    FROM todo_deps
                    ORDER BY todo_id, depends_on
                    """;
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string todoId = reader.GetString(0);
                    string dependsOn = reader.GetString(1);
                    if (!byId.ContainsKey(todoId) || !byId.ContainsKey(dependsOn))
                    {
                        continue;
                    }

                    if (!dependencies.TryGetValue(todoId, out List<string>? values))
                    {
                        values = [];
                        dependencies[todoId] = values;
                    }
                    if (!values.Contains(dependsOn, StringComparer.Ordinal))
                    {
                        values.Add(dependsOn);
                    }
                }

                foreach ((string taskId, List<string> values) in dependencies)
                {
                    byId[taskId].DependsOn = [.. values];
                }
            }

            succeeded = true;
            return new CopilotTaskPlanRead(Succeeded: true, Tasks: [.. tasks]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is SqliteException or
                DllNotFoundException or
                EntryPointNotFoundException or
                TypeInitializationException or
                InvalidOperationException)
        {
            if (_reportedFailures.TryAdd(sessionId, 0))
            {
                _log?.Invoke($"chat: could not read local tasks for {sessionId} ({ex.Message}).");
            }
            return CopilotTaskPlanRead.Failed;
        }
        finally
        {
            if (succeeded)
            {
                _reportedFailures.TryRemove(sessionId, out _);
            }
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string NormalizeStatus(string? status) =>
        status?.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "done" or "complete" or "completed" => "completed",
            "in_progress" or "active" or "running" => "in_progress",
            "blocked" => "blocked",
            "failed" or "error" => "failed",
            _ => "pending",
        };
}

internal readonly record struct CopilotTaskPlanRead(bool Succeeded, ChatTaskEntry[]? Tasks)
{
    public static CopilotTaskPlanRead Unavailable => new(Succeeded: true, Tasks: null);

    public static CopilotTaskPlanRead Failed => new(Succeeded: false, Tasks: null);
}
