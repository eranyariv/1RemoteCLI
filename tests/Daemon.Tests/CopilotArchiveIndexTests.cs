using Microsoft.Data.Sqlite;
using OneRemoteCli.Daemon.Chat;

namespace OneRemoteCli.Daemon.Tests;

public sealed class CopilotArchiveIndexTests
{
    [Fact]
    public async Task FindsArchivedSessionsAndArchivedWorkspaceChats()
    {
        string path = TemporaryDatabasePath();

        try
        {
            await CreateDatabaseAsync(
                path,
                """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, archived_at TEXT);
                CREATE TABLE workspaces (
                    id TEXT PRIMARY KEY,
                    session_id TEXT,
                    archived_at TEXT
                );
                CREATE TABLE workspace_side_chats (
                    workspace_id TEXT NOT NULL,
                    session_id TEXT NOT NULL
                );

                INSERT INTO sessions VALUES ('active-chat', NULL);
                INSERT INTO sessions VALUES ('archived-chat', '2026-08-20');
                INSERT INTO sessions VALUES ('active-workspace-chat', NULL);
                INSERT INTO sessions VALUES ('archived-workspace-chat', NULL);
                INSERT INTO sessions VALUES ('archived-side-chat', NULL);

                INSERT INTO workspaces VALUES ('active', 'active-workspace-chat', NULL);
                INSERT INTO workspaces VALUES ('archived', 'archived-workspace-chat', '2026-08-20');
                INSERT INTO workspace_side_chats VALUES ('archived', 'archived-side-chat');
                """);

            var index = new CopilotArchiveIndex(databasePath: path);

            HashSet<string> archived = await index.ReadArchivedSessionIdsAsync();

            Assert.Equal(3, archived.Count);
            Assert.Contains("archived-chat", archived);
            Assert.Contains("archived-workspace-chat", archived);
            Assert.Contains("archived-side-chat", archived);
            Assert.DoesNotContain("active-chat", archived);
            Assert.DoesNotContain("active-workspace-chat", archived);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingDatabaseLeavesAllAcpSessionsVisible()
    {
        string path = TemporaryDatabasePath();
        var index = new CopilotArchiveIndex(databasePath: path);

        HashSet<string> archived = await index.ReadArchivedSessionIdsAsync();

        Assert.Empty(archived);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MissingWorkspaceTablesStillFiltersDirectSessionArchives()
    {
        string path = TemporaryDatabasePath();

        try
        {
            await CreateDatabaseAsync(
                path,
                """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, archived_at TEXT);
                INSERT INTO sessions VALUES ('archived-chat', '2026-08-20');
                """);
            var index = new CopilotArchiveIndex(databasePath: path);

            HashSet<string> archived = await index.ReadArchivedSessionIdsAsync();

            Assert.Contains("archived-chat", archived);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IncompatibleDatabaseLogsOnceAndLeavesAcpSessionsVisible()
    {
        string path = TemporaryDatabasePath();
        var messages = new List<string>();

        try
        {
            await CreateDatabaseAsync(path, "CREATE TABLE unrelated (id TEXT);");
            var index = new CopilotArchiveIndex(messages.Add, path);

            Assert.Empty(await index.ReadArchivedSessionIdsAsync());
            Assert.Empty(await index.ReadArchivedSessionIdsAsync());

            string message = Assert.Single(messages);
            Assert.Contains("archived ACP sessions may be shown", message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"1remote-copilot-archive-{Guid.NewGuid():N}.db");

    private static async Task CreateDatabaseAsync(string path, string schema)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync();
    }
}
