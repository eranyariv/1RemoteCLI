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
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                _permissions.Clear();
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
        string prompt = text.Trim();

        if (prompt.Length == 0)
        {
            throw new ArgumentException("A chat message cannot be empty.", nameof(text));
        }

        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _activeTurns);

            try
            {
                await EnsureLoadedAsync(session, CancellationToken.None).ConfigureAwait(false);
                await Client.CallAsync(
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
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"chat: prompt for {session.SessionId} failed ({ex.Message}).");
            }
            finally
            {
                Interlocked.Decrement(ref _activeTurns);
            }
        }, CancellationToken.None);
    }

    public async Task RespondPermissionAsync(
        string sessionId,
        string requestId,
        string optionId,
        CancellationToken cancellationToken = default)
    {
        AcpSession session = Get(sessionId);

        if (!_permissions.TryRemove(requestId, out PendingPermission? pending) ||
            pending.SessionId != sessionId ||
            !pending.OptionIds.Contains(optionId))
        {
            throw new InvalidOperationException("That approval is no longer pending.");
        }

        await Client.RespondPermissionAsync(pending.RpcId, optionId, cancellationToken).ConfigureAwait(false);
        ChatEvent? resolved = session.ResolvePermission(requestId, optionId);

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

        ChatEvent? changed = session.Apply(
            kind,
            String(update, "messageId") ?? String(update, "toolCallId"),
            Content(update),
            String(update, "title"),
            String(update, "status"),
            String(update, "kind"));

        if (changed is not null && session.Loaded && _sink is not null)
        {
            await _sink.OnChatTranscriptAsync(session, ChatTranscriptKind.Delta, [changed])
                .ConfigureAwait(false);
        }
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

    private static string? Content(JsonElement update)
    {
        if (update.TryGetProperty("content", out JsonElement content))
        {
            if (content.ValueKind == JsonValueKind.Object &&
                String(content, "type") == "text")
            {
                return String(content, "text");
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    Environment.NewLine,
                    content.EnumerateArray().Select(ReadContent).Where(text => text.Length > 0));
            }
        }

        return null;
    }

    private static string ReadContent(JsonElement item)
    {
        if (String(item, "type") == "diff")
        {
            return String(item, "path") is { } path ? $"Changed {path}" : "Changed a file";
        }

        if (item.TryGetProperty("content", out JsonElement nested))
        {
            return String(nested, "text") ?? string.Empty;
        }

        return String(item, "text") ?? string.Empty;
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
