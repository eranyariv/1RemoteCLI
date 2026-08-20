using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>Reads sidebar visibility state that GitHub Copilot omits from ACP listings.</summary>
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

    public async Task<HashSet<string>?> ReadVisibleSessionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            LogFailure(
                new FileNotFoundException("GitHub Copilot data.db does not exist.", _databasePath),
                "session visibility may differ from the GitHub Copilot sidebar.");
            return null;
        }

        try
        {
            await using SqliteConnection connection = Connection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string? sidebar = await ReadStateAsync(
                connection,
                "sidebar-project-groups",
                cancellationToken).ConfigureAwait(false);
            bool recent = StateString(sidebar, "viewMode") == "recent";
            HashSet<string>? recentWorkspaces = recent
                ? StateStrings(
                    await ReadStateAsync(connection, "workspace-mru", cancellationToken)
                        .ConfigureAwait(false),
                    "recentIds")
                : null;

            if (recent && recentWorkspaces is null)
            {
                throw new InvalidOperationException(
                    "The recent workspace list is missing from GitHub Copilot state.");
            }

            var visible = new HashSet<string>(StringComparer.Ordinal);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT id
                    FROM sessions
                    WHERE archived_at IS NULL
                      AND session_type = 'general_chat'
                    """;
                await ReadIdsAsync(command, visible, cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT workspace.id, workspace.session_id
                    FROM workspaces AS workspace
                    INNER JOIN sessions AS session
                        ON session.id = workspace.session_id
                    WHERE workspace.archived_at IS NULL
                      AND session.archived_at IS NULL
                      AND workspace.session_id IS NOT NULL
                    """;
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string workspaceId = reader.GetString(0);
                    if (!recent || recentWorkspaces!.Contains(workspaceId))
                    {
                        visible.Add(reader.GetString(1));
                    }
                }
            }

            Volatile.Write(ref _failureLogged, 0);
            return visible;
        }
        catch (Exception ex) when (
            ex is SqliteException or
                JsonException or
                DllNotFoundException or
                EntryPointNotFoundException or
                TypeInitializationException or
                InvalidOperationException)
        {
            LogFailure(
                ex,
                "session visibility may differ from the GitHub Copilot sidebar.");
            return null;
        }
    }

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
            await using SqliteConnection connection = Connection();
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
                    LogFailure(ex, "some archived ACP sessions may be shown.");
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
            LogFailure(ex, "some archived ACP sessions may be shown.");
        }

        return archived;
    }

    private SqliteConnection Connection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static async Task<string?> ReadStateAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    private static async Task ReadIdsAsync(
        SqliteCommand command,
        HashSet<string> ids,
        CancellationToken cancellationToken)
    {
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetString(0));
            }
        }
    }

    private static string? StateString(string? json, string property)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("state", out JsonElement state) &&
               state.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static HashSet<string>? StateStrings(string? json, string property)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("state", out JsonElement state) ||
            !state.TryGetProperty(property, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal)!;
    }

    private void LogFailure(Exception error, string consequence)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            _log?.Invoke(
                $"chat: could not read all GitHub Copilot session state ({error.Message}); " +
                consequence);
        }
    }
}
