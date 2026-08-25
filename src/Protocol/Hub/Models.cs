using MessagePack;

namespace OneRemoteCli.Protocol.Hub;

/// <summary>How much activity from one machine may wake the user's phone.</summary>
public enum NotificationLevel : byte
{
    /// <summary>Waiting-for-input prompts and session completion or failure.</summary>
    AllAttentionEvents = 0,

    /// <summary>Only events that need an explicit answer or permission.</summary>
    ActionRequired = 1,

    /// <summary>No Web Push from this machine. Live in-app state is unchanged.</summary>
    Off = 2,
}

/// <summary>
/// Whether a <see cref="TerminalOutputNotification"/> continues the stream or
/// replaces it. A snapshot resets the client's terminal before it is applied.
/// </summary>
public enum TerminalOutputKind : byte
{
    Delta = 0,
    Snapshot = 1,
}

/// <summary>The representation and input semantics a session uses.</summary>
public enum SessionKind : byte
{
    Terminal = 0,
    AgentChat = 1,
}

/// <summary>Whether a transcript frame replaces the view or updates it.</summary>
public enum ChatTranscriptKind : byte
{
    Delta = 0,
    Snapshot = 1,
}

/// <summary>The small stable set of things an agent transcript can display.</summary>
public enum ChatEventKind : byte
{
    UserMessage = 0,
    AgentMessage = 1,
    ToolCall = 2,
    Permission = 3,
    AgentThought = 4,
    Plan = 5,
}

/// <summary>One choice offered by an ACP permission or elicitation request.</summary>
[MessagePackObject]
public sealed class ChatPermissionOption
{
    [Key(0)]
    public string OptionId { get; set; } = string.Empty;

    [Key(1)]
    public string Name { get; set; } = string.Empty;

    [Key(2)]
    public string Kind { get; set; } = string.Empty;
}

/// <summary>One displayable ACP content block, flattened for the relay wire.</summary>
[MessagePackObject]
public sealed class ChatContentBlock
{
    [Key(0)]
    public string Type { get; set; } = string.Empty;

    [Key(1)]
    public string? Text { get; set; }

    [Key(2)]
    public string? Path { get; set; }

    [Key(3)]
    public string? OldText { get; set; }

    [Key(4)]
    public string? NewText { get; set; }

    [Key(5)]
    public string? TerminalId { get; set; }

    [Key(6)]
    public string? MimeType { get; set; }

    [Key(7)]
    public string? Data { get; set; }

    [Key(8)]
    public string? Uri { get; set; }

    [Key(9)]
    public string? Name { get; set; }

    [Key(10)]
    public string? Title { get; set; }

    [Key(11)]
    public string? Description { get; set; }

    [Key(12)]
    public long? Size { get; set; }

    [Key(13)]
    public string? RawJson { get; set; }
}

/// <summary>A file location associated with an ACP tool call.</summary>
[MessagePackObject]
public sealed class ChatToolLocation
{
    [Key(0)]
    public string Path { get; set; } = string.Empty;

    [Key(1)]
    public int? Line { get; set; }
}

/// <summary>One entry in the latest ACP plan snapshot.</summary>
[MessagePackObject]
public sealed class ChatPlanEntry
{
    [Key(0)]
    public string Content { get; set; } = string.Empty;

    [Key(1)]
    public string Priority { get; set; } = "medium";

    [Key(2)]
    public string Status { get; set; } = "pending";
}

/// <summary>
/// One replaceable transcript item. Re-sending an <see cref="EventId"/> updates it.
/// </summary>
[MessagePackObject]
public sealed class ChatEvent
{
    [Key(0)]
    public string EventId { get; set; } = string.Empty;

    [Key(1)]
    public ChatEventKind Kind { get; set; }

    [Key(2)]
    public string Text { get; set; } = string.Empty;

    [Key(3)]
    public string? Title { get; set; }

    [Key(4)]
    public string? Status { get; set; }

    [Key(5)]
    public string? ToolKind { get; set; }

    [Key(6)]
    public string? PermissionRequestId { get; set; }

    [Key(7)]
    public ChatPermissionOption[] Options { get; set; } = [];

    [Key(8)]
    public ChatContentBlock[] Content { get; set; } = [];

    [Key(9)]
    public ChatToolLocation[] Locations { get; set; } = [];

    [Key(10)]
    public ChatPlanEntry[] PlanEntries { get; set; } = [];

    [Key(11)]
    public string? RawInputJson { get; set; }

    [Key(12)]
    public string? RawOutputJson { get; set; }
}

/// <summary>
/// A machine registered by an agent. Machines are always scoped to the signing-in
/// user; the hub never accepts a user identifier from a request parameter.
/// </summary>
[MessagePackObject]
public sealed class MachineInfo
{
    /// <summary>Agent-generated GUID. Deliberately not the computer name, which is spoofable and not unique.</summary>
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string DisplayName { get; set; } = string.Empty;

    [Key(2)]
    public string Os { get; set; } = string.Empty;

    [Key(3)]
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>False when the machine is known but has no live agent connection.</summary>
    [Key(4)]
    public bool Online { get; set; }

    [Key(5)]
    public SessionInfo[] Sessions { get; set; } = [];
}

/// <summary>A live interactive session hosted under a wrapper on some machine.</summary>
[MessagePackObject]
public sealed class SessionInfo
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string Program { get; set; } = string.Empty;

    [Key(2)]
    public string[] Args { get; set; } = [];

    [Key(3)]
    public string Cwd { get; set; } = string.Empty;

    [Key(4)]
    public int Cols { get; set; }

    [Key(5)]
    public int Rows { get; set; }

    [Key(6)]
    public DateTimeOffset StartedAt { get; set; }

    [Key(7)]
    public string? DisplayName { get; set; }

    /// <summary>Set when the idle heuristic believes the session is waiting on the user.</summary>
    [Key(8)]
    public bool AwaitingInput { get; set; }

    /// <summary>
    /// Which CLI this session is hosting, so the phone can offer the right buttons.
    /// <para>
    /// Appended, because every field the browser reads is identified by a position
    /// nothing in the payload spells out; inserting this anywhere else would silently
    /// shift the two fields after it. An older PWA simply stops reading before it.
    /// </para>
    /// </summary>
    [Key(9)]
    public CliType CliType { get; set; }

    /// <summary>
    /// What the user decided to call this session, which outranks
    /// <see cref="DisplayName"/> everywhere a human reads one.
    /// <para>
    /// Null means nobody has renamed it. That is deliberately distinct from an empty
    /// string: the agent's name has to survive underneath so clearing the custom one
    /// reverts to it rather than leaving a blank row.
    /// </para>
    /// <para>
    /// Held by the hub rather than the agent, and so never sent by one — see
    /// <c>RelayRegistry</c>. Appended, for the reason above.
    /// </para>
    /// </summary>
    [Key(10)]
    public string? CustomName { get; set; }

    /// <summary>Lifted above the rest of the list on every one of this user's devices.</summary>
    [Key(11)]
    public bool Pinned { get; set; }

    /// <summary>Appended so older clients continue treating every session as a terminal.</summary>
    [Key(12)]
    public SessionKind Kind { get; set; }

    /// <summary>
    /// The project this session is grouped under. Null means the user's General
    /// project — the same null-means-default convention as <see cref="CustomName"/>.
    /// <para>
    /// Held by the hub, not the agent — see <c>RelayRegistry</c>'s session labels —
    /// and appended for the same forward/backward-compatibility reason as every
    /// other field added after v1.
    /// </para>
    /// </summary>
    [Key(13)]
    public string? ProjectId { get; set; }
}

/// <summary>
/// A per-user grouping of sessions. Every user always has one non-deletable
/// project, <see cref="IsGeneral"/>, that new sessions default into.
/// </summary>
[MessagePackObject]
public sealed class ProjectInfo
{
    [Key(0)]
    public string ProjectId { get; set; } = string.Empty;

    [Key(1)]
    public string Name { get; set; } = string.Empty;

    [Key(2)]
    public string? Description { get; set; }

    [Key(3)]
    public string? SiteUrl { get; set; }

    [Key(4)]
    public string? RepoUrl { get; set; }

    /// <summary>True only for the reserved, non-deletable General project.</summary>
    [Key(5)]
    public bool IsGeneral { get; set; }

    /// <summary>
    /// Bumped every time a custom icon is uploaded, so a client can cache-bust an
    /// icon URL with <c>?v=</c>. Zero means no custom icon has ever been uploaded,
    /// which is the client's cue to show the app's own default icon instead.
    /// </summary>
    [Key(6)]
    public int IconVersion { get; set; }

    [Key(7)]
    public DateTimeOffset CreatedAt { get; set; }
}
