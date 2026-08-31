using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Protocol;
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
        Assert.True(created.Loaded);
        Assert.Equal(ChatSessionState.Ready, created.ChatState);
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
        Assert.Equal(ChatSessionState.Ready, provider.Snapshot().Single().ChatState);
        Assert.Equal(["phone-one", "phone-two"], sink.TranscriptTargets);
    }

    [Fact]
    public async Task AttachSnapshotsPreservePlansFromHistoricalTurns()
    {
        AcpProvider? provider = null;

        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (method == "session/list")
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessions = new[]
                    {
                        new
                        {
                            sessionId = "history",
                            cwd = @"C:\repo",
                            title = "History",
                            updatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    nextCursor = (string?)null,
                }));
            }

            if (method == "session/load")
            {
                AcpSession session = Assert.Single(provider!.Snapshot());
                session.Apply("user_message", "turn-1", "First", null, null, null);
                session.Apply(
                    "plan",
                    null,
                    null,
                    null,
                    null,
                    null,
                    planEntries: [new() { Content = "First task", Status = "completed" }]);
                session.Apply("user_message", "turn-2", "Second", null, null, null);
                session.Apply(
                    "plan",
                    null,
                    null,
                    null,
                    null,
                    null,
                    planEntries: [new() { Content = "Second task", Status = "in_progress" }]);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            }

            throw new InvalidOperationException(method);
        }

        var sink = new RecordingChatSink();
        await using var ownedProvider = new AcpProvider(Call);
        provider = ownedProvider;
        ownedProvider.AttachSink(sink);
        await ownedProvider.RefreshAsync();

        await ownedProvider.AttachAsync("history", "phone");

        ChatEvent[] snapshot = Assert.Single(sink.Snapshots);
        ChatEvent[] plans = [.. snapshot.Where(item => item.Kind == ChatEventKind.Plan)];
        Assert.Equal(2, plans.Length);
        Assert.Equal(["turn-1", "turn-2"], plans.Select(item => item.PlanTurnId));
        Assert.Equal(["First task", "Second task"], plans.Select(item => item.PlanEntries[0].Content));
    }

    [Fact]
    public async Task RefusesAHandoffWhileAnotherCopilotProcessOwnsTheSession()
    {
        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return method switch
            {
                "session/list" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessions = new[]
                    {
                        new
                        {
                            sessionId = "desktop-owned",
                            cwd = @"C:\repo",
                            title = "Open in Desktop",
                            updatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    nextCursor = (string?)null,
                })),
                "session/load" => throw new InvalidOperationException(
                    "Session desktop-owned is already in use by another client"),
                _ => throw new InvalidOperationException(method),
            };
        }

        var sink = new RecordingUpdateSink();
        await using var provider = new AcpProvider(Call);
        provider.AttachSink(sink);
        await provider.RefreshAsync();

        AcpSession session = Assert.Single(provider.Snapshot());
        Assert.Equal(ChatSessionState.Available, session.ChatState);
        AcpPromptException notAttached = Assert.Throws<AcpPromptException>(
            () => provider.StartPrompt("desktop-owned", "continue"));
        Assert.Equal(ErrorCodes.ChatSessionUnavailable, notAttached.Code);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.AttachAsync("desktop-owned", "phone"));

        Assert.False(session.Loaded);
        Assert.Equal(ChatSessionState.Busy, session.ChatState);
        Assert.Contains("desktop-owned", sink.Updated);

        AcpPromptException refused = Assert.Throws<AcpPromptException>(
            () => provider.StartPrompt("desktop-owned", "continue"));
        Assert.Equal(ErrorCodes.ChatSessionBusy, refused.Code);
    }

    [Theory]
    [InlineData("session is already loaded", ChatSessionState.Busy)]
    [InlineData("session is in use by another process", ChatSessionState.Busy)]
    [InlineData("history file is corrupt", ChatSessionState.Unavailable)]
    public void ClassifiesSessionLoadFailures(string message, ChatSessionState expected)
    {
        Assert.Equal(
            expected,
            AcpProvider.StateForLoadFailure(new InvalidOperationException(message)));
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

    [Fact]
    public void PreservesRichAcpSessionUpdates()
    {
        var session = new AcpSession(
            "session-1",
            @"C:\repo",
            "Chat",
            DateTimeOffset.UtcNow);

        ChatEvent? thought = AcpProvider.ApplyUpdate(
            session,
            "agent_thought_chunk",
            JsonSerializer.Deserialize<JsonElement>(
                """{"messageId":"thought-1","content":{"type":"text","text":"Checking files"}}"""));
        ChatEvent? tool = AcpProvider.ApplyUpdate(
            session,
            "tool_call",
            JsonSerializer.Deserialize<JsonElement>(
                """
                {
                  "toolCallId": "tool-1",
                  "title": "Edit settings",
                  "kind": "edit",
                  "status": "in_progress",
                  "rawInput": { "path": "settings.json" },
                  "content": [
                    {
                      "type": "diff",
                      "path": "settings.json",
                      "oldText": "{\"enabled\":false}",
                      "newText": "{\"enabled\":true}"
                    }
                  ],
                  "locations": [{ "path": "settings.json", "line": 7 }]
                }
                """));
        ChatEvent? plan = AcpProvider.ApplyUpdate(
            session,
            "plan",
            JsonSerializer.Deserialize<JsonElement>(
                """
                {
                  "entries": [
                    {
                      "content": "Inspect settings",
                      "priority": "high",
                      "status": "completed",
                      "taskId": "settings"
                    },
                    {
                      "content": "Edit settings",
                      "priority": "medium",
                      "status": "failed",
                      "taskId": "edit-settings",
                      "parentTaskId": "settings",
                      "depth": 1
                    }
                  ]
                }
                """));

        Assert.Equal(ChatEventKind.AgentThought, thought!.Kind);
        Assert.Equal("Checking files", thought.Text);
        Assert.Equal("settings.json", Assert.Single(tool!.Content).Path);
        Assert.Equal(7, Assert.Single(tool.Locations).Line);
        Assert.Equal(
            "settings.json",
            JsonSerializer.Deserialize<JsonElement>(tool.RawInputJson!).GetProperty("path").GetString());
        Assert.Equal(ChatEventKind.Plan, plan!.Kind);
        Assert.Equal("completed", plan.PlanEntries[0].Status);
        Assert.Equal("edit-settings", plan.PlanEntries[1].TaskId);
        Assert.Equal("settings", plan.PlanEntries[1].ParentTaskId);
        Assert.Equal(1, plan.PlanEntries[1].Depth);
        Assert.Equal("failed", plan.PlanEntries[1].Status);
    }

    [Fact]
    public void DeepProviderPlansClampRatherThanLosingTheirHierarchy()
    {
        var entries = new JsonArray();
        for (int depth = 0; depth <= 17; depth++)
        {
            entries.Add(new JsonObject
            {
                ["content"] = $"Task {depth}",
                ["status"] = "pending",
                ["depth"] = depth,
            });
        }

        var session = new AcpSession("session-1", @"C:\repo", "Chat", DateTimeOffset.UtcNow);
        ChatEvent plan = AcpProvider.ApplyUpdate(
            session,
            "plan",
            JsonSerializer.SerializeToElement(new JsonObject { ["entries"] = entries }))!;

        Assert.Equal(16, plan.PlanEntries[17].Depth);
        Assert.Equal(plan.PlanEntries[15].TaskId, plan.PlanEntries[17].ParentTaskId);
    }

    [Fact]
    public void HistoricalUserAttachmentsAreReducedToMetadataWhileAgentImagesRemainDisplayable()
    {
        var session = new AcpSession(
            "session-1",
            @"C:\repo",
            "Chat",
            DateTimeOffset.UtcNow);
        string imageData = Convert.ToBase64String(new byte[1024]);
        string blobData = Convert.ToBase64String(new byte[2048]);

        ChatEvent? user = AcpProvider.ApplyUpdate(
            session,
            "user_message",
            JsonSerializer.Deserialize<JsonElement>(
                $$"""
                {
                  "messageId": "user-1",
                  "content": [
                    { "type": "text", "text": "Inspect these" },
                    {
                      "type": "image",
                      "mimeType": "image/png",
                      "data": "{{imageData}}",
                      "uri": "attachment://1remotecli/image-1/phone%20photo.png"
                    },
                    {
                      "type": "resource",
                      "resource": {
                        "uri": "attachment://1remotecli/file-1/notes.txt",
                        "mimeType": "text/plain",
                        "text": "private file contents"
                      }
                    },
                    {
                      "type": "resource",
                      "resource": {
                        "uri": "attachment://1remotecli/file-2/archive.zip",
                        "mimeType": "application/zip",
                        "blob": "{{blobData}}"
                      }
                    },
                    {
                      "type": "image",
                      "mimeType": "image/png",
                      "data": "{{imageData}}",
                      "uri": "data:image/png;base64,{{imageData}}"
                    }
                  ]
                }
                """));
        ChatEvent? agent = AcpProvider.ApplyUpdate(
            session,
            "agent_message_chunk",
            JsonSerializer.Deserialize<JsonElement>(
                $$"""
                {
                  "messageId": "agent-1",
                  "content": {
                    "type": "image",
                    "mimeType": "image/png",
                    "data": "{{imageData}}"
                  }
                }
                """));

        Assert.Equal("Inspect these", user!.Text);
        Assert.DoesNotContain("private file contents", user.Text, StringComparison.Ordinal);
        Assert.Equal(5, user.Content.Length);
        Assert.Equal("text", user.Content[0].Type);
        Assert.Equal("phone photo.png", user.Content[1].Name);
        Assert.Equal(1024, user.Content[1].Size);
        Assert.Equal("notes.txt", user.Content[2].Name);
        Assert.Equal(Encoding.UTF8.GetByteCount("private file contents"), user.Content[2].Size);
        Assert.Equal("archive.zip", user.Content[3].Name);
        Assert.Equal(2048, user.Content[3].Size);
        Assert.Equal("Image attachment", user.Content[4].Name);
        Assert.Null(user.Content[4].Uri);
        Assert.All(user.Content, block =>
        {
            Assert.Null(block.Data);
            if (block.Type != "text")
            {
                Assert.Null(block.Text);
                Assert.Null(block.RawJson);
            }
        });

        Assert.Equal(imageData, Assert.Single(agent!.Content).Data);
    }

    [Fact]
    public async Task PublishesPromptAsAUserTurnBeforeCallingAcp()
    {
        RecordingPromptSink? sink = null;
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
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "prompted" }),
                "session/prompt" when sink?.Events.Count == 1 =>
                    JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }),
                "session/prompt" => throw new InvalidOperationException("Prompt was not published first."),
                _ => throw new InvalidOperationException(method),
            });
        }

        await using var provider = new AcpProvider(Call);
        sink = new RecordingPromptSink();
        provider.AttachSink(sink);
        AcpSession session = await provider.CreateAsync(@"C:\repo", "Prompted");
        session.Loaded = true;

        await provider.PromptAsync("prompted", "  Continue from the phone  ");

        ChatEvent user = Assert.Single(sink.Events);
        Assert.Equal(ChatEventKind.UserMessage, user.Kind);
        Assert.Equal("Continue from the phone", user.Text);
        Assert.Equal("session/prompt", calls[^1].Method);
        Assert.Equal(
            "Continue from the phone",
            calls[^1].Parameters["prompt"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Continue from the phone", Assert.Single(session.Snapshot()).Text);
    }

    [Fact]
    public async Task ReportsWhenAPromptStartsAndFinishes()
    {
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<JsonElement> Call(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return method switch
            {
                "session/new" => Task.FromResult(
                    JsonSerializer.SerializeToElement(new { sessionId = "active" })),
                "session/prompt" => Prompt(),
                _ => throw new InvalidOperationException(method),
            };
        }

        Task<JsonElement> Prompt()
        {
            promptStarted.TrySetResult();
            return finishPrompt.Task;
        }

        await using var provider = new AcpProvider(Call);
        AcpSession session = await provider.CreateAsync(@"C:\repo", "Active");
        session.Loaded = true;
        var activity = new List<int>();
        provider.ActivityChanged += () => activity.Add(provider.ActiveTurns);

        Task prompting = provider.PromptAsync("active", "Keep going");
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.ActiveTurns);

        finishPrompt.SetResult(JsonSerializer.SerializeToElement(new { }));
        await prompting;

        Assert.Equal(0, provider.ActiveTurns);
        Assert.Equal([1, 0], activity);
    }

    [Fact]
    public async Task SendsAttachmentsAsOrderedAcpContentAndEchoesOnlyTheirMetadata()
    {
        var calls = new List<(string Method, JsonObject Parameters)>();

        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add((method, parameters));

            return Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "attached" }),
                "session/prompt" => JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }),
                _ => throw new InvalidOperationException(method),
            });
        }

        var sink = new RecordingPromptSink();
        await using var provider = new AcpProvider(Call);
        provider.AttachSink(sink);

        AcpSession session = await provider.CreateAsync(@"C:\repo", "Attached");
        session.Loaded = true;
        session.UpdateCapabilities(new AcpPromptCapabilities(Image: true, EmbeddedContext: true));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
        await provider.PromptAsync(
            "attached",
            "  look at these  ",
            [
                new ChatAttachmentContent(Guid.NewGuid().ToString(), "photo.png", "image/png", png),
                new ChatAttachmentContent(
                    Guid.NewGuid().ToString(),
                    "notes.txt",
                    "text/plain",
                    "hello"u8.ToArray()),
            ],
            CancellationToken.None);

        JsonArray prompt = calls.Single(call => call.Method == "session/prompt")
            .Parameters["prompt"]!.AsArray();

        Assert.Equal(3, prompt.Count);
        Assert.Equal("look at these", prompt[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", prompt[1]!["type"]!.GetValue<string>());
        Assert.Equal("resource", prompt[2]!["type"]!.GetValue<string>());
        Assert.Equal("hello", prompt[2]!["resource"]!["text"]!.GetValue<string>());

        // The bubble names the files. It must not carry a byte of them: the transcript
        // is broadcast to every attached device and replayed on every snapshot.
        ChatEvent echoed = Assert.Single(sink.Events);
        Assert.Equal("look at these", echoed.Text);
        Assert.Equal(2, echoed.Content.Length);
        Assert.Equal(["photo.png", "notes.txt"], echoed.Content.Select(block => block.Name));
        Assert.All(echoed.Content, block => Assert.Null(block.Data));
        Assert.All(echoed.Content, block => Assert.Null(block.Text));
        Assert.DoesNotContain(
            Convert.ToBase64String(png),
            JsonSerializer.Serialize(echoed.Content.Select(block => block.Uri)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentOnlyPromptsAreAllowedAndTextOnlyPromptsAreUnchanged()
    {
        var calls = new List<(string Method, JsonObject Parameters)>();

        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add((method, parameters));

            return Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "attached" }),
                "session/prompt" => JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }),
                _ => throw new InvalidOperationException(method),
            });
        }

        await using var provider = new AcpProvider(Call);
        AcpSession session = await provider.CreateAsync(@"C:\repo", "Attached");
        session.Loaded = true;
        session.UpdateCapabilities(new AcpPromptCapabilities(Image: true, EmbeddedContext: true));

        await provider.PromptAsync(
            "attached",
            string.Empty,
            [
                new ChatAttachmentContent(
                    Guid.NewGuid().ToString(),
                    "photo.png",
                    "image/png",
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ],
            CancellationToken.None);

        JsonArray attachmentOnly = calls.Last().Parameters["prompt"]!.AsArray();
        Assert.Equal("image", Assert.Single(attachmentOnly)!["type"]!.GetValue<string>());

        await provider.PromptAsync("attached", "just words");

        JsonArray textOnly = calls.Last().Parameters["prompt"]!.AsArray();
        JsonNode text = Assert.Single(textOnly)!;
        Assert.Equal("text", text["type"]!.GetValue<string>());
        Assert.Equal("just words", text["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnsupportedAttachmentIsRefusedBeforeAnythingIsSent()
    {
        var calls = new List<string>();

        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            calls.Add(method);
            return Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "attached" }),
                _ => throw new InvalidOperationException(method),
            });
        }

        await using var provider = new AcpProvider(Call);
        AcpSession session = await provider.CreateAsync(@"C:\repo", "Attached");
        session.Loaded = true;

        // Nothing negotiated: the default is no attachment support at all.
        AcpPromptException refused = Assert.Throws<AcpPromptException>(() => provider.StartPrompt(
            "attached",
            "look",
            [
                new ChatAttachmentContent(
                    Guid.NewGuid().ToString(),
                    "photo.png",
                    "image/png",
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ]));

        Assert.Equal(ErrorCodes.AttachmentUnsupported, refused.Code);
        Assert.DoesNotContain("session/prompt", calls);

        AcpPromptException missing = Assert.Throws<AcpPromptException>(() =>
            provider.StartPrompt("no-such-chat", "hello", []));
        Assert.Equal(ErrorCodes.SessionNotFound, missing.Code);
    }

    [Fact]
    public async Task DiscoveredSessionsAreToldWhenCapabilitiesChange()
    {
        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(
                    new { sessionId = parameters["cwd"]!.GetValue<string>() }),
                _ => throw new InvalidOperationException(method),
            });

        var sink = new RecordingUpdateSink();
        await using var provider = new AcpProvider(Call);
        provider.AttachSink(sink);

        AcpSession first = await provider.CreateAsync(@"C:\one", "One");
        AcpSession second = await provider.CreateAsync(@"C:\two", "Two");
        Assert.Equal(AcpPromptCapabilities.None, first.PromptCapabilities);

        var negotiated = new AcpPromptCapabilities(Image: true, EmbeddedContext: false);
        await provider.ApplyCapabilitiesAsync(negotiated);

        Assert.Equal(negotiated, first.PromptCapabilities);
        Assert.Equal(negotiated, second.PromptCapabilities);
        Assert.Equal([first.SessionId, second.SessionId], sink.Updated.Order());

        // Re-negotiating the same answer changes nothing, so nothing is broadcast.
        sink.Updated.Clear();
        await provider.ApplyCapabilitiesAsync(negotiated);
        Assert.Empty(sink.Updated);

        // Losing the ACP process has to reach the phone too, or a composer keeps
        // offering a picker whose upload can no longer be staged.
        await provider.ApplyCapabilitiesAsync(AcpPromptCapabilities.None);
        Assert.Equal(AcpPromptCapabilities.None, first.PromptCapabilities);
        Assert.Equal(2, sink.Updated.Count);
    }

    private static JsonElement Page(int start, int count, string? nextCursor)    {
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

    /// <summary>Notes which sessions the relay was told changed, and nothing else.</summary>
    private sealed class RecordingUpdateSink : IAgentChatSink
    {
        public List<string> Updated { get; } = [];

        public ValueTask OnChatOpenedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatUpdatedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default)
        {
            Updated.Add(session.SessionId);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnChatClosedAsync(
            AcpSession session,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatTranscriptAsync(
            AcpSession session,
            ChatTranscriptKind kind,
            ChatEvent[] events,
            string? targetConnectionId = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnChatAttentionAsync(
            AcpSession session,
            bool awaitingInput,
            string? hint,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingChatSink : IAgentChatSink
    {
        public List<string?> TranscriptTargets { get; } = [];

        public List<ChatEvent[]> Snapshots { get; } = [];

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
            Snapshots.Add(events);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnChatAttentionAsync(
            AcpSession session,
            bool awaitingInput,
            string? hint,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingPromptSink : IAgentChatSink
    {
        public List<ChatEvent> Events { get; } = [];

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
            Assert.Equal(ChatTranscriptKind.Delta, kind);
            Events.AddRange(events);
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
