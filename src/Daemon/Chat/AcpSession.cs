using OneRemoteCli.Protocol.Hub;

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
    private long _syntheticId;
    private long _seq;

    public string SessionId { get; } = sessionId;

    public string Cwd { get; private set; } = cwd;

    public string Title { get; private set; } = title;

    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;

    public string Program { get; } = program;

    public CliType CliType { get; } = cliType;

    public SemaphoreSlim LoadGate { get; } = new(1, 1);

    public bool Loaded { get; set; }

    public bool AwaitingInput { get; private set; }

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
            _seq++;
            AwaitingInput = false;
        }
    }

    /// <summary>Applies one ACP session update and returns the replacement item to relay.</summary>
    public ChatEvent? Apply(string updateKind, string? id, string? text, string? title, string? status, string? toolKind)
    {
        lock (_gate)
        {
            ChatEvent? changed = updateKind switch
            {
                "user_message_chunk" => ApplyMessage(ChatEventKind.UserMessage, id, text),
                "agent_message_chunk" => ApplyMessage(ChatEventKind.AgentMessage, id, text),
                "tool_call" or "tool_call_update" => ApplyTool(id, text, title, status, toolKind),
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

    private ChatEvent ApplyMessage(ChatEventKind kind, string? messageId, string? text)
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
        _openMessageId = id;
        _openMessageKind = kind;
        return item;
    }

    private ChatEvent? ApplyTool(
        string? toolCallId,
        string? detail,
        string? title,
        string? status,
        string? toolKind)
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

        _openMessageId = null;
        _openMessageKind = null;
        return item;
    }

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
        };
}
