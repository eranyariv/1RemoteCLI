using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Daemon.Chat;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AcpProviderTests
{
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
}
