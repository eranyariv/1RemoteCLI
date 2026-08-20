using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AcpProviderTests
{
    [Fact]
    public async Task CreatesAndKeepsANewAcpSession()
    {
        var calls = new List<(string Method, JsonObject Parameters)>();

        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add((method, parameters));

            return Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "created" }),
                "session/list" => JsonSerializer.SerializeToElement(new
                {
                    sessions = Array.Empty<object>(),
                    nextCursor = (string?)null,
                }),
                _ => throw new InvalidOperationException(method),
            });
        }

        await using var provider = new AcpProvider(Call);

        AcpSession created = await provider.CreateAsync(@"C:\repo", "My repo");
        await provider.RefreshAsync();

        Assert.Equal("created", created.SessionId);
        Assert.Equal("My repo", created.Title);
        Assert.Same(created, Assert.Single(provider.Snapshot()));
        Assert.Equal("session/new", calls[0].Method);
        Assert.Equal(@"C:\repo", calls[0].Parameters["cwd"]!.GetValue<string>());
        Assert.Empty(calls[0].Parameters["mcpServers"]!.AsArray());
    }

    [Fact]
    public async Task ReusesLoadedSessionWhenAnotherClientAttaches()
    {
        int loads = 0;

        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(method switch
            {
                "session/list" => JsonSerializer.SerializeToElement(new
                {
                    sessions = new[]
                    {
                        new
                        {
                            sessionId = "shared",
                            cwd = @"C:\repo",
                            title = "Shared chat",
                            updatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    nextCursor = (string?)null,
                }),
                "session/load" when ++loads == 1 => JsonSerializer.SerializeToElement(new { }),
                "session/load" => throw new InvalidOperationException("Session shared is already loaded"),
                _ => throw new InvalidOperationException(method),
            });
        }

        var sink = new RecordingChatSink();
        await using var provider = new AcpProvider(Call);
        provider.AttachSink(sink);
        await provider.RefreshAsync();

        await provider.AttachAsync("shared", "phone-one");
        await provider.AttachAsync("shared", "phone-two");

        Assert.Equal(1, loads);
        Assert.Equal(["phone-one", "phone-two"], sink.TranscriptTargets);
    }

    [Fact]
    public async Task CopilotSidebarVisibilityReplacesTheBroadRecentFilter()
    {
        string path = TemporaryDatabasePath();

        try
        {
            await CreateDatabaseAsync(
                path,
                """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    session_type TEXT,
                    archived_at TEXT
                );
                CREATE TABLE workspaces (
                    id TEXT PRIMARY KEY,
                    session_id TEXT,
                    archived_at TEXT
                );
                CREATE TABLE app_state (key TEXT PRIMARY KEY, value TEXT);

                INSERT INTO sessions VALUES ('visible', 'general_chat', NULL);
                INSERT INTO sessions VALUES ('hidden', 'project', NULL);
                INSERT INTO app_state VALUES (
                    'sidebar-project-groups',
                    '{"state":{"viewMode":"recent"}}'
                );
                INSERT INTO app_state VALUES (
                    'workspace-mru',
                    '{"state":{"recentIds":[]}}'
                );
                """);
            var index = new CopilotArchiveIndex(databasePath: path);

            Task<JsonElement> Call(
                string method,
                JsonObject parameters,
                CancellationToken cancellationToken)
            {
                Assert.Equal("session/list", method);
                Assert.Empty(parameters);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessions = new[]
                    {
                        new
                        {
                            sessionId = "visible",
                            cwd = @"C:\visible",
                            title = "Visible",
                            updatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                        },
                        new
                        {
                            sessionId = "hidden",
                            cwd = @"C:\hidden",
                            title = "Hidden",
                            updatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    nextCursor = (string?)null,
                }));
            }

            await using var provider = new AcpProvider(
                Call,
                hideArchivedSessions: true,
                copilotIndex: index);

            await provider.RefreshAsync();

            AcpSession visible = Assert.Single(provider.Snapshot());
            Assert.Equal("visible", visible.SessionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IncompatibleCopilotDatabaseFallsBackWithoutHidingSessions()
    {
        string path = TemporaryDatabasePath();

        try
        {
            await CreateDatabaseAsync(path, "CREATE TABLE unrelated (id TEXT);");
            var index = new CopilotArchiveIndex(databasePath: path);

            Task<JsonElement> Call(
                string method,
                JsonObject parameters,
                CancellationToken cancellationToken)
            {
                Assert.Equal("session/list", method);
                Assert.Empty(parameters);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessions = new[]
                    {
                        new
                        {
                            sessionId = "fallback",
                            cwd = @"C:\fallback",
                            title = "Fallback",
                            updatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    nextCursor = (string?)null,
                }));
            }

            await using var provider = new AcpProvider(
                Call,
                hideArchivedSessions: true,
                copilotIndex: index);

            await provider.RefreshAsync();

            Assert.Equal("fallback", Assert.Single(provider.Snapshot()).SessionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FollowsSessionListCursorUpToOneHundredSessions()
    {
        var cursors = new List<string?>();

        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            Assert.Equal("session/list", method);
            cancellationToken.ThrowIfCancellationRequested();

            string? cursor = parameters["cursor"]?.GetValue<string>();
            cursors.Add(cursor);

            return Task.FromResult(cursor switch
            {
                null => Page(start: 0, count: 50, nextCursor: "NTA="),
                "NTA=" => Page(start: 50, count: 60, nextCursor: null),
                _ => throw new InvalidOperationException($"Unexpected cursor {cursor}."),
            });
        }

        await using var provider = new AcpProvider(Call);

        await provider.RefreshAsync();

        Assert.Equal([null, "NTA="], cursors);
        Assert.Equal(100, provider.Count);
        Assert.Contains(provider.Snapshot(), session => session.SessionId == "session-099");
        Assert.DoesNotContain(provider.Snapshot(), session => session.SessionId == "session-100");
    }

    private static JsonElement Page(int start, int count, string? nextCursor)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var sessions = Enumerable.Range(start, count).Select(index => new
        {
            sessionId = $"session-{index:000}",
            cwd = $@"C:\work\{index:000}",
            title = $"Session {index:000}",
            updatedAt = now.AddMinutes(-index),
        });

        return JsonSerializer.SerializeToElement(new { sessions, nextCursor });
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"1remote-copilot-provider-{Guid.NewGuid():N}.db");

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

    private sealed class RecordingChatSink : IAgentChatSink
    {
        public List<string?> TranscriptTargets { get; } = [];

        public ValueTask OnChatOpenedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatUpdatedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatClosedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatTranscriptAsync(
            AcpSession session,
            ChatTranscriptKind kind,
            ChatEvent[] events,
            string? targetConnectionId = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ChatTranscriptKind.Snapshot, kind);
            TranscriptTargets.Add(targetConnectionId);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnChatAttentionAsync(
            AcpSession session,
            bool awaitingInput,
            string? hint,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
