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

/// <summary>How the user chose a session's destination project.</summary>
public enum SessionProjectMoveKind : byte
{
    Manual = 0,
    Suggested = 1,
    Always = 2,
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

/// <summary>
/// Whether this agent process can safely drive an ACP session.
/// <para>
/// ACP sessions are disk-backed and can be resumed by another process, but only as
/// a sequential handoff. They are not live-shared conversations. The state lets a
/// phone wait for <see cref="Ready"/> rather than sending into a session still owned
/// by Copilot Desktop or another ACP client.
/// </para>
/// </summary>
public enum ChatSessionState
{
    /// <summary>An older agent did not report cross-process ownership state.</summary>
    Unknown = 0,

    /// <summary>The session was discovered but this ACP process has not loaded it yet.</summary>
    Available = 1,

    /// <summary>This ACP process loaded the session and can safely accept prompts.</summary>
    Ready = 2,

    /// <summary>Another Copilot process currently owns the session.</summary>
    Busy = 3,

    /// <summary>The ACP process or session could not currently be loaded.</summary>
    Unavailable = 4,
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

    /// <summary>Stable within this plan so a replacement snapshot updates the same row.</summary>
    [Key(3)]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Optional parent task. Null keeps ordinary ACP plans as a flat list.</summary>
    [Key(4)]
    public string? ParentTaskId { get; set; }

    /// <summary>Resolved nesting depth, bounded by the daemon before it reaches the phone.</summary>
    [Key(5)]
    public int Depth { get; set; }
}

/// <summary>One read-only task from a Copilot Desktop session's local task database.</summary>
[MessagePackObject]
public sealed class ChatTaskEntry
{
    [Key(0)]
    public string TaskId { get; set; } = string.Empty;

    [Key(1)]
    public string Title { get; set; } = string.Empty;

    [Key(2)]
    public string Status { get; set; } = "pending";

    /// <summary>Task ids that must finish before this task can start.</summary>
    [Key(3)]
    public string[] DependsOn { get; set; } = [];
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

    /// <summary>User-message event that owns this plan, or null for a session-level plan.</summary>
    [Key(13)]
    public string? PlanTurnId { get; set; }

    /// <summary>Monotonic replacement number for this turn's atomic plan snapshot.</summary>
    [Key(14)]
    public int PlanRevision { get; set; }
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

    /// <summary>
    /// What the ACP agent behind an <see cref="SessionKind.AgentChat"/> session said
    /// it can be sent, negotiated once per ACP process.
    /// <para>
    /// Null on every terminal session, and on any chat session relayed by an agent
    /// that predates protocol version 6 — which is exactly what an older peer's
    /// decoder produces when it stops reading before this field. Null therefore has
    /// to mean "no attachment support", never "unknown, try it and see": a composer
    /// that offers a picker the agent cannot honour would fail after the user has
    /// already chosen a photo.
    /// </para>
    /// </summary>
    [Key(14)]
    public ChatCapabilities? ChatCapabilities { get; set; }

    /// <summary>A learned destination offered while this session remains in General.</summary>
    [Key(15)]
    public string? SuggestedProjectId { get; set; }

    /// <summary>How many matching sessions accepted this exact suggestion.</summary>
    [Key(16)]
    public int SuggestedProjectMoves { get; set; }

    /// <summary>
    /// Whether an agent-chat session is exclusively loaded by this agent process.
    /// Unknown is the safe default for terminal sessions and agents predating protocol 8.
    /// </summary>
    [Key(17)]
    public ChatSessionState ChatState { get; set; }

    /// <summary>
    /// Read-only tasks from the session-local Copilot database. Null means no compatible,
    /// non-empty task table is available and the phone must disable its Plan view.
    /// </summary>
    [Key(18)]
    public ChatTaskEntry[]? LocalTasks { get; set; }
}

/// <summary>
/// The subset of ACP <c>promptCapabilities</c> the phone needs in order to decide
/// what its composer may offer. Both flags are strict: absent means false.
/// </summary>
[MessagePackObject]
public sealed class ChatCapabilities
{
    /// <summary>The agent accepts ACP <c>image</c> content blocks in a prompt.</summary>
    [Key(0)]
    public bool Image { get; set; }

    /// <summary>The agent accepts ACP embedded <c>resource</c> content blocks in a prompt.</summary>
    [Key(1)]
    public bool EmbeddedContext { get; set; }
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
