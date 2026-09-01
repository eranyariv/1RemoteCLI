using Microsoft.Data.Sqlite;
using OneRemoteCli.Daemon.Chat;

namespace OneRemoteCli.Daemon.Tests;

public sealed class CopilotTaskPlanIndexTests
{
    [Fact]
    public async Task ReadsTasksStatusesAndDependenciesWithoutChangingTheDatabase()
    {
        string sessionId = Guid.NewGuid().ToString();
        string root = TemporaryRoot();
        string databasePath = Path.Combine(root, sessionId, "session.db");

        try
        {
            await CreateDatabaseAsync(
                databasePath,
                """
                CREATE TABLE todos (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    description TEXT,
                    status TEXT,
                    created_at TEXT,
                    updated_at TEXT
                );
                CREATE TABLE todo_deps (
                    todo_id TEXT,
                    depends_on TEXT,
                    PRIMARY KEY(todo_id, depends_on)
                );

                INSERT INTO todos VALUES ('inspect', 'Inspect implementation', '', 'done', '', '');
                INSERT INTO todos VALUES ('build', 'Build plan view', '', 'active', '', '');
                INSERT INTO todos VALUES ('ship', 'Ship release', '', 'blocked', '', '');
                INSERT INTO todo_deps VALUES ('build', 'inspect');
                INSERT INTO todo_deps VALUES ('ship', 'build');
                INSERT INTO todo_deps VALUES ('missing', 'inspect');
                """);
            var index = new CopilotTaskPlanIndex(sessionStateRoot: root);

            CopilotTaskPlanRead result = await index.ReadAsync(sessionId);

            Assert.True(result.Succeeded);
            Assert.Collection(
                Assert.IsType<OneRemoteCli.Protocol.Hub.ChatTaskEntry[]>(result.Tasks),
                task =>
                {
                    Assert.Equal("inspect", task.TaskId);
                    Assert.Equal("Inspect implementation", task.Title);
                    Assert.Equal("completed", task.Status);
                    Assert.Empty(task.DependsOn);
                },
                task =>
                {
                    Assert.Equal("build", task.TaskId);
                    Assert.Equal("in_progress", task.Status);
                    Assert.Equal(["inspect"], task.DependsOn);
                },
                task =>
                {
                    Assert.Equal("ship", task.TaskId);
                    Assert.Equal("blocked", task.Status);
                    Assert.Equal(["build"], task.DependsOn);
                });
            Assert.Equal(3, await ScalarAsync(databasePath, "SELECT COUNT(*) FROM todos"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MissingEmptyOrIncompatibleTaskTablesAreUnavailable()
    {
        string root = TemporaryRoot();
        string missing = Guid.NewGuid().ToString();
        string empty = Guid.NewGuid().ToString();
        string incompatible = Guid.NewGuid().ToString();

        try
        {
            await CreateDatabaseAsync(
                Path.Combine(root, empty, "session.db"),
                "CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT, status TEXT);");
            await CreateDatabaseAsync(
                Path.Combine(root, incompatible, "session.db"),
                "CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT);");
            var index = new CopilotTaskPlanIndex(sessionStateRoot: root);

            Assert.Null((await index.ReadAsync(missing)).Tasks);
            Assert.Null((await index.ReadAsync(empty)).Tasks);
            Assert.Null((await index.ReadAsync(incompatible)).Tasks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RefusesSessionIdsThatCouldEscapeTheSessionStateRoot()
    {
        string root = TemporaryRoot();
        var index = new CopilotTaskPlanIndex(sessionStateRoot: root);

        CopilotTaskPlanRead result = await index.ReadAsync(@"..\outside");

        Assert.True(result.Succeeded);
        Assert.Null(result.Tasks);
        Assert.False(Directory.Exists(root));
    }

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"1remote-task-plans-{Guid.NewGuid():N}");

    private static async Task CreateDatabaseAsync(string path, string schema)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
