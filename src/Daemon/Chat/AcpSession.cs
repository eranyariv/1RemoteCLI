using OneRemoteCli.Protocol.Hub;
using System.Security.Cryptography;
using System.Text;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>An agent session and the transcript reconstructed from ACP updates.</summary>
public sealed class AcpSession(
    string sessionId,
    string cwd,
    string title,
    DateTimeOffset updatedAt,
    string program = "GitHub Copilot",
    CliType cliType = CliType.CopilotCli)
{
    private readonly object _gate = new();
    private readonly List<ChatEvent> _events = [];
    private readonly Dictionary<string, ChatEvent> _byId = new(StringComparer.Ordinal);
    private string? _openMessageId;
    private ChatEventKind? _openMessageKind;
    private string? _pendingPromptId;
    private string? _pendingPromptText;
    private string? _suppressedPromptEchoMessageId;
    private int _pendingPromptEchoLength;
    private bool _pendingPromptHasAttachments;
    private string? _currentTurnId;
    private long _syntheticId;
    private long _seq;
    private ChatTaskEntry[]? _localTasks;

    public string SessionId { get; } = sessionId;

    public string Cwd { get; private set; } = cwd;

    public string Title { get; private set; } = title;

    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;

    public string Program { get; } = program;

    public CliType CliType { get; } = cliType;

    public SemaphoreSlim LoadGate { get; } = new(1, 1);

    internal SemaphoreSlim TaskPlanGate { get; } = new(1, 1);

    public bool Loaded { get; set; }

    public ChatSessionState ChatState { get; private set; } = ChatSessionState.Available;

    /// <summary>Returns true when the ACP ownership state actually changed.</summary>
    public bool SetChatState(ChatSessionState state)
    {
        lock (_gate)
        {
            if (ChatState == state)
            {
                return false;
            }

            ChatState = state;
            return true;
        }
    }

    /// <summary>
    /// What the ACP process behind this session accepts in a prompt.
    /// <para>
    /// Held per session rather than only on the provider because it travels to the
    /// phone inside <c>SessionInfo</c>, and because it changes underneath a session
    /// that is already discovered: a restarted ACP process re-negotiates, and a
    /// composer still offering a camera button would be offering one nothing can
    /// honour.
    /// </para>
    /// </summary>
    public AcpPromptCapabilities PromptCapabilities { get; private set; } = AcpPromptCapabilities.None;

    /// <summary>Returns true when the value actually moved, so callers can avoid a needless broadcast.</summary>
    public bool UpdateCapabilities(AcpPromptCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        lock (_gate)
        {
            if (PromptCapabilities == capabilities)
            {
                return false;
            }

            PromptCapabilities = capabilities;
            return true;
        }
    }

    public bool AwaitingInput { get; private set; }

    public ChatTaskEntry[]? LocalTasks
    {
        get
        {
            lock (_gate)
            {
                return CopyTasks(_localTasks);
            }
        }
    }

    public bool UpdateLocalTasks(ChatTaskEntry[]? tasks)
    {
        lock (_gate)
        {
            if (TasksEqual(_localTasks, tasks))
            {
                return false;
            }

            _localTasks = CopyTasks(tasks);
            return true;
        }
    }

    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    public bool UpdateMetadata(string cwd, string title, DateTimeOffset updatedAt)
    {
        lock (_gate)
        {
            bool changed = Cwd != cwd || Title != title || UpdatedAt != updatedAt;
            Cwd = cwd;
            Title = title;
            UpdatedAt = updatedAt;
            return changed;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
            _byId.Clear();
            _openMessageId = null;
            _openMessageKind = null;
            _currentTurnId = null;
            ClearPendingPromptEcho();
            _seq++;
            AwaitingInput = false;
        }
    }

    /// <summary>Applies one ACP session update and returns the replacement item to relay.</summary>
    public ChatEvent? Apply(
        string updateKind,
        string? id,
        string? text,
        string? title,
        string? status,
        string? toolKind,
        ChatContentBlock[]? content = null,
        ChatToolLocation[]? locations = null,
        ChatPlanEntry[]? planEntries = null,
        string? rawInputJson = null,
        string? rawOutputJson = null)
    {
        lock (_gate)
        {
            if (updateKind is not "user_message" and not "user_message_chunk")
            {
                ClearPendingPromptEcho(clearMessageId: true);
            }

            ChatEvent? changed = updateKind switch
            {
                "user_message" => ApplyUserMessage(id, text, content, replace: true),
                "user_message_chunk" => ApplyUserMessage(id, text, content, replace: false),
                "agent_message_chunk" => ApplyMessage(ChatEventKind.AgentMessage, id, text, content),
                "agent_thought_chunk" => ApplyMessage(ChatEventKind.AgentThought, id, text, content),
                "tool_call" or "tool_call_update" => ApplyTool(
                    id,
                    text,
                    title,
                    status,
                    toolKind,
                    content,
                    locations,
                    rawInputJson,
                    rawOutputJson),
                "plan" => ApplyPlan(planEntries),
                _ => null,
            };

            if (changed is not null)
            {
                _seq++;
                UpdatedAt = DateTimeOffset.UtcNow;
            }

            return changed is null ? null : Copy(changed);
        }
    }

    /// <summary>
    /// Adds the prompt owned by this ACP client before the agent starts streaming.
    /// Some ACP agents replay user messages on load but do not echo a live
    /// <c>session/prompt</c>, so waiting for an update loses both the bubble and the
    /// boundary between adjacent assistant turns.
    /// <para>
    /// <paramref name="attachments"/> is a metadata-only summary — name, type, size.
    /// The bytes that were sent are never put in the transcript: they would be echoed
    /// back to every attached device, written into logs the moment anything failed,
    /// and re-sent on every snapshot, all to show the user a file they chose from
    /// their own phone.
    /// </para>
    /// </summary>
    public ChatEvent AddUserPrompt(string text, ChatContentBlock[]? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 && (attachments is null || attachments.Length == 0))
        {
            throw new ArgumentException("A prompt needs text, an attachment, or both.", nameof(text));
        }

        lock (_gate)
        {
            string eventId = $"prompt:{++_syntheticId}";
            var item = new ChatEvent
            {
                EventId = eventId,
                Kind = ChatEventKind.UserMessage,
                Text = text,
                Content = attachments is null ? [] : [.. attachments.Select(Copy)],
            };

            Upsert(item);
            _openMessageId = eventId;
            _openMessageKind = ChatEventKind.UserMessage;
            _pendingPromptId = eventId;
            _currentTurnId = eventId;
            _pendingPromptText = text;
            _suppressedPromptEchoMessageId = null;
            _pendingPromptEchoLength = 0;
            _pendingPromptHasAttachments = attachments is { Length: > 0 };
            _seq++;
            UpdatedAt = DateTimeOffset.UtcNow;
            return Copy(item);
        }
    }

    public ChatEvent AddPermission(
        string requestId,
        string toolCallId,
        string title,
        ChatPermissionOption[] options)
    {
        lock (_gate)
        {
            string eventId = $"permission:{requestId}";
            var item = new ChatEvent
            {
                EventId = eventId,
                Kind = ChatEventKind.Permission,
                Title = title,
                Text = "Approval required",
                Status = "pending",
                PermissionRequestId = requestId,
                Options = options,
                ToolKind = toolCallId,
            };

            Upsert(item);
            AwaitingInput = true;
            _openMessageId = null;
            _openMessageKind = null;
            _seq++;
            return Copy(item);
        }
    }

    public ChatEvent AddElicitation(
        string requestId,
        string? toolCallId,
        string title,
        string message,
        ChatPermissionOption[] options)
    {
        lock (_gate)
        {
            string eventId = $"elicitation:{requestId}";
            var item = new ChatEvent
            {
                EventId = eventId,
                // Choice elicitations intentionally use the existing permission shape so
                // a cached older PWA can still render and answer their options.
                Kind = ChatEventKind.Permission,
                Title = title,
                Text = message,
                Status = "pending",
                PermissionRequestId = requestId,
                Options = options,
                ToolKind = toolCallId,
            };

            Upsert(item);
            AwaitingInput = true;
            _openMessageId = null;
            _openMessageKind = null;
            _seq++;
            return Copy(item);
        }
    }

    public ChatEvent? ResolvePermission(string requestId, string status)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue($"permission:{requestId}", out ChatEvent? item))
            {
                return null;
            }

            item.Status = status;
            AwaitingInput = HasPendingInput();
            _seq++;
            return Copy(item);
        }
    }

    public ChatEvent? ResolveElicitation(string requestId, string status)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue($"elicitation:{requestId}", out ChatEvent? item))
            {
                return null;
            }

            item.Status = status;
            AwaitingInput = HasPendingInput();
            _seq++;
            return Copy(item);
        }
    }

    public ChatEvent[] CancelPendingInputs()
    {
        lock (_gate)
        {
            ChatEvent[] pending =
            [
                .. _events.Where(
                    item => item.Kind == ChatEventKind.Permission && item.Status == "pending"),
            ];
            foreach (ChatEvent item in pending)
            {
                item.Status = "cancelled";
            }

            if (pending.Length > 0)
            {
                AwaitingInput = false;
                _seq++;
            }

            return [.. pending.Select(Copy)];
        }
    }

    public ChatEvent[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _events.Select(Copy)];
        }
    }

    private ChatEvent ApplyMessage(
        ChatEventKind kind,
        string? messageId,
        string? text,
        ChatContentBlock[]? content)
    {
        string id = !string.IsNullOrWhiteSpace(messageId)
            ? messageId
            : _openMessageKind == kind && _openMessageId is not null
                ? _openMessageId
                : $"message:{++_syntheticId}";

        if (!_byId.TryGetValue(id, out ChatEvent? item))
        {
            item = new ChatEvent { EventId = id, Kind = kind };
            Upsert(item);
        }

        item.Text += text ?? string.Empty;
        if (content is not null)
        {
            item.Content = [.. item.Content, .. content.Select(Copy)];
        }
        _openMessageId = id;
        _openMessageKind = kind;
        return item;
    }

    private ChatEvent? ApplyUserMessage(
        string? messageId,
        string? text,
        ChatContentBlock[]? content,
        bool replace)
    {
        if (SuppressPromptEcho(messageId, text, replace))
        {
            return null;
        }

        ChatEvent item = ApplyMessage(ChatEventKind.UserMessage, messageId, text, content);
        _currentTurnId = item.EventId;
        if (replace)
        {
            item.Text = text ?? string.Empty;
            item.Content = content is null ? item.Content : [.. content.Select(Copy)];
        }
        return item;
    }

    private bool SuppressPromptEcho(string? messageId, string? text, bool replace)
    {
        if (_suppressedPromptEchoMessageId is not null &&
            messageId == _suppressedPromptEchoMessageId)
        {
            return true;
        }

        // An attachment can be replayed before or after the text block, and some ACP
        // agents omit the message id on chunks. Keep the pending echo alive until the
        // first non-user update so neither ordering can put the selected file's bytes
        // into the transcript.
        if (_pendingPromptId is not null &&
            _pendingPromptHasAttachments &&
            _byId.ContainsKey(_pendingPromptId) &&
            (_pendingPromptText is { Length: 0 } || string.IsNullOrEmpty(text)))
        {
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                _suppressedPromptEchoMessageId = messageId;
            }

            return true;
        }

        if (_pendingPromptId is null ||
            _pendingPromptText is null ||
            _pendingPromptText.Length == 0 ||
            !_byId.ContainsKey(_pendingPromptId) ||
            string.IsNullOrEmpty(text))
        {
            ClearPendingPromptEcho(clearMessageId: true);
            return false;
        }

        bool matches = replace
            ? text == _pendingPromptText
            : _pendingPromptText.AsSpan(_pendingPromptEchoLength).StartsWith(text, StringComparison.Ordinal);
        if (!matches)
        {
            bool belongsToPrompt =
                _suppressedPromptEchoMessageId is not null &&
                messageId == _suppressedPromptEchoMessageId;
            ClearPendingPromptEcho(clearMessageId: !belongsToPrompt);
            return belongsToPrompt;
        }

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            _suppressedPromptEchoMessageId = messageId;
        }

        _pendingPromptEchoLength = replace
            ? _pendingPromptText.Length
            : _pendingPromptEchoLength + text.Length;
        if (_pendingPromptEchoLength >= _pendingPromptText.Length &&
            !_pendingPromptHasAttachments)
        {
            ClearPendingPromptEcho(clearMessageId: false);
        }
        return true;
    }

    private void ClearPendingPromptEcho(bool clearMessageId = true)
    {
        _pendingPromptId = null;
        _pendingPromptText = null;
        _pendingPromptEchoLength = 0;
        _pendingPromptHasAttachments = false;
        if (clearMessageId)
        {
            _suppressedPromptEchoMessageId = null;
        }
    }

    private ChatEvent? ApplyTool(
        string? toolCallId,
        string? detail,
        string? title,
        string? status,
        string? toolKind,
        ChatContentBlock[]? content,
        ChatToolLocation[]? locations,
        string? rawInputJson,
        string? rawOutputJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        if (!_byId.TryGetValue(toolCallId, out ChatEvent? item))
        {
            item = new ChatEvent
            {
                EventId = toolCallId,
                Kind = ChatEventKind.ToolCall,
            };
            Upsert(item);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            item.Text = detail;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            item.Title = title;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            item.Status = status;
        }

        if (!string.IsNullOrWhiteSpace(toolKind))
        {
            item.ToolKind = toolKind;
        }

        if (content is not null)
        {
            item.Content = [.. content.Select(Copy)];
        }

        if (locations is not null)
        {
            item.Locations = [.. locations.Select(Copy)];
        }

        if (rawInputJson is not null)
        {
            item.RawInputJson = rawInputJson;
        }

        if (rawOutputJson is not null)
        {
            item.RawOutputJson = rawOutputJson;
        }

        _openMessageId = null;
        _openMessageKind = null;
        return item;
    }

    private ChatEvent ApplyPlan(ChatPlanEntry[]? entries)
    {
        string turnId = _currentTurnId ?? "session";
        string id = $"plan:{turnId}";
        if (!_byId.TryGetValue(id, out ChatEvent? item))
        {
            item = new ChatEvent
            {
                EventId = id,
                Kind = ChatEventKind.Plan,
                Title = "Plan",
                PlanTurnId = _currentTurnId,
            };
            Upsert(item);
        }

        item.PlanEntries = EnrichPlanEntries(entries ?? [], item.PlanEntries);
        item.Text = string.Join(Environment.NewLine, item.PlanEntries.Select(entry => entry.Content));
        item.PlanRevision++;
        _openMessageId = null;
        _openMessageKind = null;
        return item;
    }

    private static ChatPlanEntry[] EnrichPlanEntries(
        IReadOnlyList<ChatPlanEntry> incoming,
        IReadOnlyList<ChatPlanEntry> previous)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var priorByContent = previous
            .GroupBy(entry => NormalizedTaskContent(entry.Content), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<string>(group.Select(entry => entry.TaskId)),
                StringComparer.Ordinal);
        var occurrenceByContent = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new ChatPlanEntry[incoming.Count];

        for (int index = 0; index < incoming.Count; index++)
        {
            ChatPlanEntry source = incoming[index];
            string normalized = NormalizedTaskContent(source.Content);
            occurrenceByContent.TryGetValue(normalized, out int occurrence);
            occurrenceByContent[normalized] = occurrence + 1;

            string taskId = source.TaskId.Trim();
            if (taskId.Length == 0 &&
                priorByContent.TryGetValue(normalized, out Queue<string>? priorIds))
            {
                while (priorIds.Count > 0 && taskId.Length == 0)
                {
                    string candidate = priorIds.Dequeue();
                    if (!usedIds.Contains(candidate))
                    {
                        taskId = candidate;
                    }
                }
            }

            if (taskId.Length == 0 || usedIds.Contains(taskId))
            {
                taskId = StableTaskId(normalized, occurrence);
                int collision = 1;
                while (usedIds.Contains(taskId))
                {
                    taskId = StableTaskId(normalized, occurrence + collision++);
                }
            }
            usedIds.Add(taskId);

            result[index] = new ChatPlanEntry
            {
                Content = source.Content,
                Priority = NormalizePriority(source.Priority),
                Status = NormalizePlanStatus(source.Status),
                TaskId = taskId,
                ParentTaskId = string.IsNullOrWhiteSpace(source.ParentTaskId)
                    ? null
                    : source.ParentTaskId.Trim(),
                Depth = Math.Clamp(source.Depth, 0, 16),
            };
        }

        ResolvePlanHierarchy(result);
        return result;
    }

    private static void ResolvePlanHierarchy(ChatPlanEntry[] entries)
    {
        var resolved = new Dictionary<string, ChatPlanEntry>(StringComparer.Ordinal);
        var ancestors = new ChatPlanEntry?[17];

        foreach (ChatPlanEntry entry in entries)
        {
            if (entry.ParentTaskId is not null &&
                resolved.TryGetValue(entry.ParentTaskId, out ChatPlanEntry? parent))
            {
                entry.Depth = Math.Min(parent.Depth + 1, 16);
            }
            else if (entry.Depth > 0 && ancestors[entry.Depth - 1] is ChatPlanEntry depthParent)
            {
                entry.ParentTaskId = depthParent.TaskId;
                entry.Depth = Math.Min(depthParent.Depth + 1, 16);
            }
            else
            {
                entry.ParentTaskId = null;
                entry.Depth = 0;
            }

            ancestors[entry.Depth] = entry;
            for (int depth = entry.Depth + 1; depth < ancestors.Length; depth++)
            {
                ancestors[depth] = null;
            }
            resolved[entry.TaskId] = entry;
        }
    }

    private static string StableTaskId(string normalizedContent, int occurrence)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{normalizedContent}\n{occurrence}"));
        return $"task:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string NormalizedTaskContent(string content) =>
        string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private static string NormalizePriority(string priority) =>
        priority.Trim().ToLowerInvariant() switch
        {
            "high" => "high",
            "low" => "low",
            _ => "medium",
        };

    private static string NormalizePlanStatus(string status) =>
        status.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "completed" or "complete" or "done" => "completed",
            "in_progress" or "running" or "active" => "in_progress",
            "failed" or "failure" or "error" => "failed",
            _ => "pending",
        };

    private void Upsert(ChatEvent item)
    {
        if (!_byId.ContainsKey(item.EventId))
        {
            _events.Add(item);
        }

        _byId[item.EventId] = item;
    }

    private bool HasPendingInput() =>
        _events.Any(
            item =>
                item.Kind == ChatEventKind.Permission && item.Status == "pending");

    private static ChatEvent Copy(ChatEvent item) =>
        new()
        {
            EventId = item.EventId,
            Kind = item.Kind,
            Text = item.Text,
            Title = item.Title,
            Status = item.Status,
            ToolKind = item.ToolKind,
            PermissionRequestId = item.PermissionRequestId,
            Options =
            [
                .. item.Options.Select(option => new ChatPermissionOption
                {
                    OptionId = option.OptionId,
                    Name = option.Name,
                    Kind = option.Kind,
                }),
            ],
            Content = [.. item.Content.Select(Copy)],
            Locations = [.. item.Locations.Select(Copy)],
            PlanEntries = [.. item.PlanEntries.Select(Copy)],
            RawInputJson = item.RawInputJson,
            RawOutputJson = item.RawOutputJson,
            PlanTurnId = item.PlanTurnId,
            PlanRevision = item.PlanRevision,
        };

    private static ChatTaskEntry[]? CopyTasks(ChatTaskEntry[]? tasks) =>
        tasks is null
            ? null
            :
            [
                .. tasks.Select(task => new ChatTaskEntry
                {
                    TaskId = task.TaskId,
                    Title = task.Title,
                    Status = task.Status,
                    DependsOn = [.. task.DependsOn],
                }),
            ];

    private static bool TasksEqual(ChatTaskEntry[]? left, ChatTaskEntry[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        if (left.Length != right.Length)
        {
            return false;
        }

        return left.Zip(right).All(pair =>
            pair.First.TaskId == pair.Second.TaskId &&
            pair.First.Title == pair.Second.Title &&
            pair.First.Status == pair.Second.Status &&
            pair.First.DependsOn.SequenceEqual(pair.Second.DependsOn, StringComparer.Ordinal));
    }

    private static ChatContentBlock Copy(ChatContentBlock item) =>
        new()
        {
            Type = item.Type,
            Text = item.Text,
            Path = item.Path,
            OldText = item.OldText,
            NewText = item.NewText,
            TerminalId = item.TerminalId,
            MimeType = item.MimeType,
            Data = item.Data,
            Uri = item.Uri,
            Name = item.Name,
            Title = item.Title,
            Description = item.Description,
            Size = item.Size,
            RawJson = item.RawJson,
        };

    private static ChatToolLocation Copy(ChatToolLocation item) =>
        new() { Path = item.Path, Line = item.Line };

    private static ChatPlanEntry Copy(ChatPlanEntry item) =>
        new()
        {
            Content = item.Content,
            Priority = item.Priority,
            Status = item.Status,
            TaskId = item.TaskId,
            ParentTaskId = item.ParentTaskId,
            Depth = item.Depth,
        };
}
