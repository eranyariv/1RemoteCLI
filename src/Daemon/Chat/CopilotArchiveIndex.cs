using Microsoft.Data.Sqlite;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>Reads archive state that GitHub Copilot omits from ACP session listings.</summary>
internal sealed class CopilotArchiveIndex(Action<string>? log = null, string? databasePath = null)
{
    private static readonly string[] ArchiveQueries =
    [
        """
        SELECT id
        FROM sessions
        WHERE archived_at IS NOT NULL
        """,
        """
        SELECT session_id
        FROM workspaces
        WHERE archived_at IS NOT NULL
          AND session_id IS NOT NULL
        """,
        """
        SELECT side_chat.session_id
        FROM workspace_side_chats AS side_chat
        INNER JOIN workspaces AS workspace
            ON workspace.id = side_chat.workspace_id
        WHERE workspace.archived_at IS NOT NULL
        """,
    ];

    private readonly string _databasePath = databasePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot",
        "data.db");
    private readonly Action<string>? _log = log;
    private int _failureLogged;

    public async Task<HashSet<string>> ReadArchivedSessionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var archived = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(_databasePath))
        {
            return archived;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool failed = false;
            foreach (string query in ArchiveQueries)
            {
                try
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = query;
                    await using SqliteDataReader reader =
                        await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!reader.IsDBNull(0))
                        {
                            archived.Add(reader.GetString(0));
                        }
                    }
                }
                catch (SqliteException ex)
                {
                    failed = true;
                    LogFailure(ex);
                }
            }

            if (!failed)
            {
                Volatile.Write(ref _failureLogged, 0);
            }
        }
        catch (Exception ex) when (
            ex is SqliteException or
                DllNotFoundException or
                EntryPointNotFoundException or
                TypeInitializationException or
                InvalidOperationException)
        {
            LogFailure(ex);
        }

        return archived;
    }

    private void LogFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            _log?.Invoke(
                $"chat: could not read all GitHub Copilot archive state ({error.Message}); " +
                "some archived ACP sessions may be shown.");
        }
    }
}
