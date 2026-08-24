using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AcpSessionTests
{
    [Fact]
    public void ConsecutiveChunksWithoutIdsAreCombinedByRole()
    {
        var session = Create();

        session.Apply("user_message_chunk", null, "Fix ", null, null, null);
        ChatEvent? user = session.Apply("user_message_chunk", null, "this", null, null, null);
        ChatEvent? agent = session.Apply("agent_message_chunk", null, "Sure", null, null, null);

        ChatEvent[] transcript = session.Snapshot();
        Assert.Equal(2, transcript.Length);
        Assert.Equal("Fix this", user!.Text);
        Assert.Equal(ChatEventKind.UserMessage, transcript[0].Kind);
        Assert.Equal("Sure", agent!.Text);
        Assert.NotEqual(transcript[0].EventId, transcript[1].EventId);
    }

    [Fact]
    public void ExplicitMessageIdsReplaceTheSameTranscriptItem()
    {
        var session = Create();

        session.Apply("agent_message_chunk", "answer", "Hello", null, null, null);
        ChatEvent? changed = session.Apply("agent_message_chunk", "answer", " world", null, null, null);

        ChatEvent item = Assert.Single(session.Snapshot());
        Assert.Equal("answer", item.EventId);
        Assert.Equal("Hello world", changed!.Text);
        Assert.Equal("Hello world", item.Text);
    }

    [Fact]
    public void ToolUpdatesReplaceToolCallsWithoutLosingPriorFields()
    {
        var session = Create();

        session.Apply(
            "tool_call",
            "tool-1",
            "dotnet test",
            "Run tests",
            "pending",
            "execute",
            content: [new() { Type = "text", Text = "Starting tests" }],
            locations: [new() { Path = @"C:\repo\Tests.cs", Line = 42 }],
            rawInputJson: """{"command":"dotnet test"}""");
        ChatEvent? changed = session.Apply(
            "tool_call_update",
            "tool-1",
            "All tests passed",
            null,
            "completed",
            null,
            content: [new() { Type = "text", Text = "All tests passed" }],
            rawOutputJson: """{"exitCode":0}""");

        ChatEvent item = Assert.Single(session.Snapshot());
        Assert.Equal("Run tests", item.Title);
        Assert.Equal("All tests passed", item.Text);
        Assert.Equal("completed", changed!.Status);
        Assert.Equal("execute", item.ToolKind);
        Assert.Equal("All tests passed", Assert.Single(item.Content).Text);
        Assert.Equal(42, Assert.Single(item.Locations).Line);
        Assert.Equal("""{"command":"dotnet test"}""", item.RawInputJson);
        Assert.Equal("""{"exitCode":0}""", item.RawOutputJson);
    }

    [Fact]
    public void ThoughtsAndPlansRemainDistinctFromAgentMessages()
    {
        var session = Create();

        ChatEvent? thought = session.Apply(
            "agent_thought_chunk",
            "thought-1",
            "Inspecting the project",
            null,
            null,
            null,
            content: [new() { Type = "text", Text = "Inspecting the project" }]);
        ChatEvent? plan = session.Apply(
            "plan",
            null,
            null,
            null,
            null,
            null,
            planEntries:
            [
                new() { Content = "Read the project", Priority = "high", Status = "completed" },
                new() { Content = "Run tests", Priority = "medium", Status = "in_progress" },
            ]);

        Assert.Equal(ChatEventKind.AgentThought, thought!.Kind);
        Assert.Equal("Inspecting the project", thought.Text);
        Assert.Equal(ChatEventKind.Plan, plan!.Kind);
        Assert.Equal(2, plan.PlanEntries.Length);
        Assert.Equal("Run tests", plan.PlanEntries[1].Content);
    }

    [Fact]
    public void PermissionsSetAndClearAttention()
    {
        var session = Create();
        ChatPermissionOption[] options =
        [
            new() { OptionId = "yes", Name = "Allow", Kind = "allow_once" },
        ];

        ChatEvent added = session.AddPermission("request-1", "tool-1", "Run tests", options);
        ChatEvent? resolved = session.ResolvePermission("request-1", "yes");

        Assert.StartsWith("permission:", added.EventId);
        Assert.False(session.AwaitingInput);
        Assert.Equal("yes", resolved!.Status);
        Assert.Equal("yes", Assert.Single(session.Snapshot()).Status);
    }

    [Fact]
    public void ElicitationsSetAndClearAttention()
    {
        var session = Create();
        ChatPermissionOption[] options =
        [
            new() { OptionId = "postgres", Name = "PostgreSQL", Kind = "select" },
        ];

        ChatEvent added = session.AddElicitation(
            "request-1",
            "ask-user-1",
            "Database",
            "Which database?",
            options);
        ChatEvent? resolved = session.ResolveElicitation("request-1", "postgres");

        Assert.Equal(ChatEventKind.Permission, added.Kind);
        Assert.True(added.Options.Length == 1);
        Assert.False(session.AwaitingInput);
        Assert.Equal("postgres", resolved!.Status);
    }

    [Fact]
    public void CancelsEveryPendingInputWhenTheAcpConnectionEnds()
    {
        var session = Create();
        session.AddPermission(
            "permission-1",
            "tool-1",
            "Run tests",
            [new() { OptionId = "yes", Name = "Allow", Kind = "allow_once" }]);
        session.AddElicitation(
            "question-1",
            "ask-user-1",
            "Database",
            "Which database?",
            [new() { OptionId = "sqlite", Name = "SQLite", Kind = "select" }]);

        ChatEvent[] cancelled = session.CancelPendingInputs();

        Assert.Equal(2, cancelled.Length);
        Assert.All(cancelled, item => Assert.Equal("cancelled", item.Status));
        Assert.False(session.AwaitingInput);
    }

    [Fact]
    public void SnapshotsAreDefensiveCopies()
    {
        var session = Create();
        session.Apply("agent_message_chunk", "answer", "original", null, null, null);

        ChatEvent item = Assert.Single(session.Snapshot());
        item.Text = "changed";

        Assert.Equal("original", Assert.Single(session.Snapshot()).Text);
    }

    private static AcpSession Create() =>
        new("session-1", @"C:\repo", "Chat", DateTimeOffset.UtcNow);
}
