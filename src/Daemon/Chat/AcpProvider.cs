using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>
/// Discovers desktop-agent sessions through a public ACP server and makes their
/// structured transcripts available to the relay.
/// </summary>
public sealed class AcpProvider : IAsyncDisposable
{
    private const int MaximumSessions = 100;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _createdSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPermission> _permissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingElicitation> _elicitations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshRequested = new(0);
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonElement>>? _call;
    private readonly Action<string>? _log;
    private readonly AcpSettings _settings = AcpSettings.FromEnvironment();
    private readonly CopilotArchiveIndex _copilotIndex;
    private AcpClient? _client;
    private IAgentChatSink? _sink;
    private int _activeTurns;
    private int _hideArchivedSessions;

    public AcpProvider(Action<string>? log = null, bool hideArchivedSessions = true)
        : this(log, hideArchivedSessions, call: null)
    {
    }

    internal AcpProvider(
        Func<string, JsonObject, CancellationToken, Task<JsonElement>> call,
        bool hideArchivedSessions = false,
        CopilotArchiveIndex? copilotIndex = null)
        : this(log: null, hideArchivedSessions, call, copilotIndex)
    {
        ArgumentNullException.ThrowIfNull(call);
    }

    private AcpProvider(
        Action<string>? log,
        bool hideArchivedSessions,
        Func<string, JsonObject, CancellationToken, Task<JsonElement>>? call,
        CopilotArchiveIndex? copilotIndex = null)
    {
        _log = log;
        _copilotIndex = copilotIndex ?? new CopilotArchiveIndex(log);
        _hideArchivedSessions = hideArchivedSessions ? 1 : 0;
        _call = call;
    }

    /// <summary>Raised when the discovered chat count changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when an ACP turn starts or finishes.</summary>
    public event Action? ActivityChanged;

    public int Count => _sessions.Count;

    public int ActiveTurns => Volatile.Read(ref _activeTurns);

    public CliType CliType => _settings.CliType;

    public bool HideArchivedSessions => Volatile.Read(ref _hideArchivedSessions) != 0;

    public IReadOnlyList<AcpSession> Snapshot() =>
        [.. _sessions.Values.OrderByDescending(session => session.UpdatedAt)];

    public bool TryGet(string sessionId, out AcpSession session) =>
        _sessions.TryGetValue(sessionId, out session!);

    public void AttachSink(IAgentChatSink sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public void SetHideArchivedSessions(bool hide)
    {
        int value = hide ? 1 : 0;
        if (Interlocked.Exchange(ref _hideArchivedSessions, value) != value)
        {
            _refreshRequested.Release();
        }
    }

    /// <summary>Creates a new ACP chat and publishes it before returning.</summary>
    public async Task<AcpSession> CreateAsync(
        string cwd,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        string fullCwd = Path.GetFullPath(cwd);
        JsonElement result = await CallAsync(
            "session/new",
            new JsonObject
            {
                ["cwd"] = fullCwd,
                ["mcpServers"] = new JsonArray(),
            },
            cancellationToken).ConfigureAwait(false);
        string? sessionId = String(result, "sessionId");

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                $"{_settings.DisplayName} ACP returned no session id for the new chat.");
        }

        string title = string.IsNullOrWhiteSpace(displayName)
            ? $"{_settings.DisplayName} chat"
            : displayName.Trim();
        var session = new AcpSession(
            sessionId,
            fullCwd,
            title,
            DateTimeOffset.UtcNow,
            _settings.Program,
            _settings.CliType);

        if (!_sessions.TryAdd(sessionId, session))
        {
            return _sessions[sessionId];
        }

        _createdSessions[sessionId] = 0;
        if (_sink is not null)
        {
            await _sink.OnChatOpenedAsync(session, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke();
        return session;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Environment.GetEnvironmentVariable("ONEREMOTE_ACP") is string disabled &&
            disabled.Trim() is "0" or "off" or "false")
        {
            _log?.Invoke("chat: ACP discovery is disabled by ONEREMOTE_ACP.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _client = await AcpClient.StartAsync(
                    _settings.Executable,
                    _settings.Arguments,
                    _settings.DisplayName,
                    _log,
                    cancellationToken).ConfigureAwait(false);
                _client.SessionUpdate += OnSessionUpdateAsync;
                _client.PermissionRequested += OnPermissionRequestedAsync;
                _client.ElicitationRequested += OnElicitationRequestedAsync;

                while (!cancellationToken.IsCancellationRequested)
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    await _refreshRequested.WaitAsync(RefreshInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"chat: {_settings.DisplayName} ACP is unavailable ({ex.Message}); retrying.");
            }
            finally
            {
                AcpClient? client = Interlocked.Exchange(ref _client, null);
                if (client is not null)
                {
                    client.SessionUpdate -= OnSessionUpdateAsync;
                    client.PermissionRequested -= OnPermissionRequestedAsync;
                    client.ElicitationRequested -= OnElicitationRequestedAsync;
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                await CancelPendingInputsAsync().ConfigureAwait(false);
                foreach (AcpSession session in _sessions.Values)
                {
                    session.Loaded = false;
                }
            }

            try
            {
                await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task AttachAsync(
        string sessionId,
        string? clientConnectionId,
        CancellationToken cancellationToken = default)
    {
        AcpSession session = Get(sessionId);
        await session.LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!session.Loaded)
            {
                session.Reset();
                await CallAsync(
                    "session/load",
                    new JsonObject
                    {
                        ["sessionId"] = session.SessionId,
                        ["cwd"] = session.Cwd,
                        ["mcpServers"] = new JsonArray(),
                    },
                    cancellationToken).ConfigureAwait(false);
                session.Loaded = true;
            }

            if (_sink is not null)
            {
                await _sink.OnChatTranscriptAsync(
                    session,
                    ChatTranscriptKind.Snapshot,
                    session.Snapshot(),
                    clientConnectionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            session.LoadGate.Release();
        }
    }

    public void StartPrompt(string sessionId, string text)
    {
        AcpSession session = Get(sessionId);
        string prompt = NormalizePrompt(text);

        _ = RunPromptAsync(session, prompt, CancellationToken.None);
    }

    internal Task PromptAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken = default) =>
        RunPromptAsync(Get(sessionId), NormalizePrompt(text), cancellationToken);

    private async Task RunPromptAsync(
        AcpSession session,
        string prompt,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeTurns);
        ActivityChanged?.Invoke();

        try
        {
            await EnsureLoadedAsync(session, cancellationToken).ConfigureAwait(false);
            ChatEvent userMessage = session.AddUserPrompt(prompt);
            if (_sink is not null)
            {
                await _sink.OnChatTranscriptAsync(
                    session,
                    ChatTranscriptKind.Delta,
                    [userMessage],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await CallAsync(
                "session/prompt",
                new JsonObject
                {
                    ["sessionId"] = session.SessionId,
                    ["prompt"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = prompt,
                        }),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"chat: prompt for {session.SessionId} failed ({ex.Message}).");
        }
        finally
        {
            Interlocked.Decrement(ref _activeTurns);
            ActivityChanged?.Invoke();
        }
    }

    private static string NormalizePrompt(string text)
    {
        string prompt = text.Trim();
        if (prompt.Length == 0)
        {
            throw new ArgumentException("A chat message cannot be empty.", nameof(text));
        }

        return prompt;
    }

    public async Task RespondPermissionAsync(
        string sessionId,
        string requestId,
        string optionId,
        CancellationToken cancellationToken = default)
    {
        AcpSession session = Get(sessionId);

        if (_permissions.TryGetValue(requestId, out PendingPermission? permission))
        {
            if (permission.SessionId != sessionId || !permission.OptionIds.Contains(optionId))
            {
                throw new InvalidOperationException("That approval is no longer pending.");
            }

            if (!_permissions.TryRemove(requestId, out PendingPermission? claimedPermission) ||
                !ReferenceEquals(permission, claimedPermission))
            {
                throw new InvalidOperationException("That approval is no longer pending.");
            }

            try
            {
                await Client.RespondPermissionAsync(permission.RpcId, optionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                if (Volatile.Read(ref _client) is not null)
                {
                    _permissions.TryAdd(requestId, permission);
                }
                throw;
            }

            ChatEvent? permissionResolved = session.ResolvePermission(requestId, optionId);
            await PublishResolutionAsync(session, permissionResolved, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_elicitations.TryGetValue(requestId, out PendingElicitation? elicitation) ||
            elicitation.SessionId != sessionId ||
            (optionId != PendingElicitation.CancelOption &&
             optionId != PendingElicitation.DeclineOption &&
             !elicitation.OptionIds.Contains(optionId)))
        {
            throw new InvalidOperationException("That question is no longer pending.");
        }

        if (!_elicitations.TryRemove(requestId, out PendingElicitation? claimedElicitation) ||
            !ReferenceEquals(elicitation, claimedElicitation))
        {
            throw new InvalidOperationException("That question is no longer pending.");
        }

        bool cancelled = optionId == PendingElicitation.CancelOption;
        bool declined = optionId == PendingElicitation.DeclineOption;
        try
        {
            await Client.RespondElicitationAsync(
                elicitation.RpcId,
                cancelled ? "cancel" : declined ? "decline" : "accept",
                cancelled || declined ? null : elicitation.FieldName,
                cancelled || declined ? null : optionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Volatile.Read(ref _client) is not null)
            {
                _elicitations.TryAdd(requestId, elicitation);
            }
            throw;
        }

        ChatEvent? resolved = session.ResolveElicitation(
            requestId,
            cancelled ? "cancelled" : declined ? "declined" : optionId);
        await PublishResolutionAsync(session, resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishResolutionAsync(
        AcpSession session,
        ChatEvent? resolved,
        CancellationToken cancellationToken)
    {
        if (_sink is not null && resolved is not null)
        {
            await _sink.OnChatTranscriptAsync(
                session,
                ChatTranscriptKind.Delta,
                [resolved],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _sink.OnChatAttentionAsync(session, session.AwaitingInput, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CancelPendingInputsAsync()
    {
        _permissions.Clear();
        _elicitations.Clear();

        foreach (AcpSession session in _sessions.Values)
        {
            ChatEvent[] cancelled = session.CancelPendingInputs();
            if (_sink is null || cancelled.Length == 0)
            {
                continue;
            }

            try
            {
                await _sink.OnChatTranscriptAsync(
                    session,
                    ChatTranscriptKind.Delta,
                    cancelled).ConfigureAwait(false);
                await _sink.OnChatAttentionAsync(
                    session,
                    awaitingInput: false,
                    hint: null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"chat: could not publish cancelled input for {session.SessionId} ({ex.Message}).");
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        bool changed = false;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - RecentWindow;
        HashSet<string>? visibleCopilotSessions = null;
        HashSet<string> archived = [];

        if (HideArchivedSessions && _settings.CliType == CliType.CopilotCli)
        {
            visibleCopilotSessions =
                await _copilotIndex.ReadVisibleSessionIdsAsync(cancellationToken).ConfigureAwait(false);
            if (visibleCopilotSessions is null)
            {
                archived =
                    await _copilotIndex.ReadArchivedSessionIdsAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        List<SessionMetadata> latest =
            await ListSessionsAsync(cutoff, archived, visibleCopilotSessions, cancellationToken)
                .ConfigureAwait(false);

        HashSet<string> visible = latest.Select(item => item.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (string discovered in visible)
        {
            _createdSessions.TryRemove(discovered, out _);
        }

        foreach (SessionMetadata item in latest)
        {
            if (_sessions.TryGetValue(item.SessionId, out AcpSession? existing))
            {
                if (existing.UpdateMetadata(item.Cwd, item.Title, item.UpdatedAt) && _sink is not null)
                {
                    await _sink.OnChatUpdatedAsync(existing, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var added = new AcpSession(
                item.SessionId,
                item.Cwd,
                item.Title,
                item.UpdatedAt,
                _settings.Program,
                _settings.CliType);
            _sessions[item.SessionId] = added;
            changed = true;

            if (_sink is not null)
            {
                await _sink.OnChatOpenedAsync(added, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach ((string id, AcpSession session) in _sessions)
        {
            if (!visible.Contains(id) &&
                !_createdSessions.ContainsKey(id) &&
                _sessions.TryRemove(id, out _))
            {
                changed = true;

                if (_sink is not null)
                {
                    await _sink.OnChatClosedAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task<List<SessionMetadata>> ListSessionsAsync(
        DateTimeOffset cutoff,
        HashSet<string> archived,
        HashSet<string>? visibleCopilotSessions,
        CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        int targetCount = visibleCopilotSessions is null
            ? MaximumSessions
            : Math.Min(MaximumSessions, visibleCopilotSessions.Count);

        if (targetCount == 0)
        {
            return [];
        }

        do
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            JsonElement result =
                await CallAsync("session/list", parameters, cancellationToken).ConfigureAwait(false);

            if (result.TryGetProperty("sessions", out JsonElement sessions))
            {
                foreach (JsonElement value in sessions.EnumerateArray())
                {
                    if (ReadMetadata(value) is { } item &&
                        (visibleCopilotSessions?.Contains(item.SessionId) ??
                         item.UpdatedAt >= cutoff && !archived.Contains(item.SessionId)))
                    {
                        discovered.TryAdd(item.SessionId, item);
                    }
                }
            }

            string? next = String(result, "nextCursor");
            if (string.IsNullOrWhiteSpace(next))
            {
                break;
            }

            if (!cursors.Add(next))
            {
                throw new InvalidOperationException(
                    $"{_settings.DisplayName} ACP repeated session-list cursor {next}.");
            }

            cursor = next;
        }
        while (discovered.Count < targetCount);

        return
        [
            .. discovered.Values
                .OrderByDescending(item => item.UpdatedAt)
                .Take(MaximumSessions),
        ];
    }

    private async Task EnsureLoadedAsync(AcpSession session, CancellationToken cancellationToken)
    {
        if (session.Loaded)
        {
            return;
        }

        await AttachAsync(session.SessionId, clientConnectionId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask OnSessionUpdateAsync(string sessionId, JsonElement update)
    {
        if (!_sessions.TryGetValue(sessionId, out AcpSession? session) ||
            !update.TryGetProperty("sessionUpdate", out JsonElement discriminator))
        {
            return;
        }

        string kind = discriminator.GetString() ?? string.Empty;

        if (kind == "session_info_update")
        {
            string title = String(update, "title") ?? session.Title;
            DateTimeOffset updated = Date(update, "updatedAt") ?? session.UpdatedAt;
            if (session.UpdateMetadata(session.Cwd, title, updated) && _sink is not null)
            {
                await _sink.OnChatUpdatedAsync(session).ConfigureAwait(false);
            }
            return;
        }

        ChatEvent? changed = ApplyUpdate(session, kind, update);

        if (changed is not null && session.Loaded && _sink is not null)
        {
            await _sink.OnChatTranscriptAsync(session, ChatTranscriptKind.Delta, [changed])
                .ConfigureAwait(false);
        }
    }

    internal static ChatEvent? ApplyUpdate(AcpSession session, string kind, JsonElement update)
    {
        ChatContentBlock[]? content = Content(update);
        return session.Apply(
            kind,
            String(update, "messageId") ?? String(update, "toolCallId"),
            ContentText(content),
            String(update, "title"),
            String(update, "status"),
            String(update, "kind"),
            content,
            Locations(update),
            PlanEntries(update),
            Json(update, "rawInput"),
            Json(update, "rawOutput"));
    }

    private async ValueTask OnPermissionRequestedAsync(JsonElement rpcId, JsonElement parameters)
    {
        string sessionId = String(parameters, "sessionId") ?? string.Empty;
        if (!_sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            return;
        }

        JsonElement toolCall = parameters.TryGetProperty("toolCall", out JsonElement call)
            ? call
            : default;
        string toolCallId = String(toolCall, "toolCallId") ?? string.Empty;
        string requestId = Guid.NewGuid().ToString("n");
        ChatPermissionOption[] options = parameters.TryGetProperty("options", out JsonElement choices)
            ? choices.EnumerateArray()
                .Select(option => new ChatPermissionOption
                {
                    OptionId = String(option, "optionId") ?? string.Empty,
                    Name = String(option, "name") ?? "Choose",
                    Kind = String(option, "kind") ?? string.Empty,
                })
                .Where(option => option.OptionId.Length > 0)
                .ToArray()
            : [];

        string title = session.Snapshot()
            .LastOrDefault(item => item.EventId == toolCallId)?.Title
            ?? $"{_settings.DisplayName} wants to use a tool";

        _permissions[requestId] = new PendingPermission(
            sessionId,
            rpcId,
            options.Select(option => option.OptionId).ToHashSet(StringComparer.Ordinal));

        ChatEvent item = session.AddPermission(requestId, toolCallId, title, options);

        if (_sink is not null)
        {
            await _sink.OnChatTranscriptAsync(session, ChatTranscriptKind.Delta, [item]).ConfigureAwait(false);
            await _sink.OnChatAttentionAsync(session, awaitingInput: true, title).ConfigureAwait(false);
        }
    }

    private async ValueTask OnElicitationRequestedAsync(JsonElement rpcId, JsonElement parameters)
    {
        AcpElicitation? elicitation = AcpElicitation.Parse(parameters);
        if (elicitation is null ||
            !_sessions.TryGetValue(elicitation.SessionId, out AcpSession? session))
        {
            await Client.RespondErrorAsync(
                rpcId,
                code: -32602,
                message: "1RemoteCLI supports single-field string elicitations.").ConfigureAwait(false);
            return;
        }

        string requestId = Guid.NewGuid().ToString("n");
        _elicitations[requestId] = new PendingElicitation(
            elicitation.SessionId,
            rpcId,
            elicitation.FieldName,
            elicitation.Options.Select(option => option.OptionId)
                .ToHashSet(StringComparer.Ordinal));

        ChatEvent item = session.AddElicitation(
            requestId,
            elicitation.ToolCallId,
            elicitation.Title,
            elicitation.Message,
            elicitation.Options);

        if (_sink is not null)
        {
            await _sink.OnChatTranscriptAsync(session, ChatTranscriptKind.Delta, [item]).ConfigureAwait(false);
            await _sink.OnChatAttentionAsync(
                session,
                awaitingInput: true,
                elicitation.Message).ConfigureAwait(false);
        }
    }

    private AcpSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out AcpSession? session)
            ? session
            : throw new InvalidOperationException($"No {_settings.DisplayName} chat session {sessionId}.");

    private AcpClient Client =>
        _client ?? throw new InvalidOperationException($"{_settings.DisplayName} ACP is not running.");

    private Task<JsonElement> CallAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken) =>
        _call is null
            ? Client.CallAsync(method, parameters, cancellationToken)
            : _call(method, parameters, cancellationToken);

    private SessionMetadata? ReadMetadata(JsonElement value)
    {
        string? id = String(value, "sessionId");
        string? cwd = String(value, "cwd");
        DateTimeOffset? updated = Date(value, "updatedAt");

        return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd) || updated is null
            ? null
            : new SessionMetadata(
                id,
                cwd,
                String(value, "title") is { Length: > 0 } title ? title : $"{_settings.DisplayName} chat",
                updated.Value);
    }

    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out JsonElement found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        DateTimeOffset.TryParse(String(value, property), out DateTimeOffset parsed) ? parsed : null;

    private static string? Json(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out JsonElement found) &&
        found.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? found.GetRawText()
            : null;

    private static ChatContentBlock[]? Content(JsonElement update)
    {
        if (!update.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            ChatContentBlock? block = ReadContent(content);
            return block is null ? [] : [block];
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<ChatContentBlock>();
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (ReadContent(item) is { } block)
                {
                    blocks.Add(block);
                }
            }

            return [.. blocks];
        }

        return [];
    }

    private static ChatContentBlock? ReadContent(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string type = String(item, "type") ?? "unknown";
        if (type == "content" &&
            item.TryGetProperty("content", out JsonElement nested))
        {
            return ReadContent(nested);
        }

        if (type == "resource" &&
            item.TryGetProperty("resource", out JsonElement resource) &&
            resource.ValueKind == JsonValueKind.Object)
        {
            return new ChatContentBlock
            {
                Type = type,
                Text = String(resource, "text"),
                Data = String(resource, "blob"),
                Uri = String(resource, "uri"),
                MimeType = String(resource, "mimeType"),
            };
        }

        return new ChatContentBlock
        {
            Type = type,
            Text = String(item, "text"),
            Path = String(item, "path"),
            OldText = String(item, "oldText") ?? String(item, "old_text"),
            NewText = String(item, "newText") ?? String(item, "new_text"),
            TerminalId = String(item, "terminalId") ?? String(item, "terminal_id"),
            MimeType = String(item, "mimeType") ?? String(item, "mime_type"),
            Data = String(item, "data"),
            Uri = String(item, "uri"),
            Name = String(item, "name"),
            Title = String(item, "title"),
            Description = String(item, "description"),
            Size = Integer(item, "size"),
            RawJson = type is "text" or "image" or "audio" or "resource_link" or "diff" or "terminal"
                ? null
                : item.GetRawText(),
        };
    }

    private static string? ContentText(ChatContentBlock[]? content)
    {
        if (content is null)
        {
            return null;
        }

        string[] lines =
        [
            .. content.Select(item => item.Type switch
            {
                "text" => item.Text,
                "diff" => item.Path is { Length: > 0 } path ? $"Changed {path}" : "Changed a file",
                "terminal" => item.TerminalId is { Length: > 0 } terminalId
                    ? $"Terminal {terminalId}"
                    : "Terminal output",
                "resource" or "resource_link" => item.Name ?? item.Uri ?? item.Text,
                _ => item.Text,
            }).Where(text => !string.IsNullOrWhiteSpace(text))!,
        ];

        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static ChatToolLocation[]? Locations(JsonElement update)
    {
        if (!update.TryGetProperty("locations", out JsonElement locations))
        {
            return null;
        }

        if (locations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. locations.EnumerateArray()
                .Select(item => new ChatToolLocation
                {
                    Path = String(item, "path") ?? string.Empty,
                    Line = LineNumber(item),
                })
                .Where(item => item.Path.Length > 0),
        ];
    }

    private static ChatPlanEntry[]? PlanEntries(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out JsonElement entries))
        {
            return null;
        }

        if (entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. entries.EnumerateArray()
                .Select(item => new ChatPlanEntry
                {
                    Content = String(item, "content") ?? string.Empty,
                    Priority = String(item, "priority") ?? "medium",
                    Status = String(item, "status") ?? "pending",
                })
                .Where(item => item.Content.Length > 0),
        ];
    }

    private static long? Integer(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out JsonElement found) &&
        found.ValueKind == JsonValueKind.Number &&
        found.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static int? LineNumber(JsonElement value)
    {
        long? line = Integer(value, "line");
        return line is >= int.MinValue and <= int.MaxValue ? (int)line.Value : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        foreach (AcpSession session in _sessions.Values)
        {
            session.LoadGate.Dispose();
        }

        _refreshRequested.Dispose();
    }

    private sealed record SessionMetadata(
        string SessionId,
        string Cwd,
        string Title,
        DateTimeOffset UpdatedAt);

    private sealed record PendingPermission(
        string SessionId,
        JsonElement RpcId,
        HashSet<string> OptionIds);

    private sealed record PendingElicitation(
        string SessionId,
        JsonElement RpcId,
        string FieldName,
        HashSet<string> OptionIds)
    {
        public const string CancelOption = "__1remote_cancel__";
        public const string DeclineOption = "__1remote_decline__";
    }

    private sealed record AcpSettings(
        string Executable,
        string[] Arguments,
        string DisplayName,
        string Program,
        CliType CliType)
    {
        public static AcpSettings FromEnvironment()
        {
            string provider = Environment.GetEnvironmentVariable("ONEREMOTE_ACP_PROVIDER")?
                .Trim().ToLowerInvariant() ?? "copilot";

            return provider switch
            {
                "claude" or "claude-code" => new(
                    Environment.GetEnvironmentVariable("ONEREMOTE_ACP_EXECUTABLE") ?? "claude-agent-acp",
                    [],
                    "Claude Code",
                    "Claude Code",
                    CliType.ClaudeCode),
                _ => new(
                    Environment.GetEnvironmentVariable("ONEREMOTE_ACP_EXECUTABLE") ?? "copilot",
                    ["--acp", "--stdio"],
                    "GitHub Copilot",
                    "GitHub Copilot",
                    CliType.CopilotCli),
            };
        }
    }
}
