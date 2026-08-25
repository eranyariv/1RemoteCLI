using MessagePack;

namespace OneRemoteCli.Protocol.Hub;

// Client to hub (spec section 5.3).

/// <summary>
/// First call a client makes. Separate from <see cref="ListMachinesRequest"/> so an
/// incompatible client is rejected before it can issue any other method.
/// </summary>
[MessagePackObject]
public sealed class ClientHandshakeRequest
{
    [Key(0)]
    public int ProtocolVersion { get; set; }

    [Key(1)]
    public string ClientVersion { get; set; } = string.Empty;
}

/// <summary>Client to hub. Lists this user's machines and their live sessions.</summary>
[MessagePackObject]
public sealed class ListMachinesRequest
{
}

/// <summary>Client to hub. Start receiving terminal output for a session.</summary>
[MessagePackObject]
public sealed class AttachSessionRequest
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    [Key(2)]
    public int Cols { get; set; }

    [Key(3)]
    public int Rows { get; set; }

    /// <summary>Last sequence this client rendered, for resume. Null requests a snapshot.</summary>
    [Key(4)]
    public long? LastSeq { get; set; }
}

/// <summary>Client to hub. Stop receiving output for a session.</summary>
[MessagePackObject]
public sealed class DetachSessionRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>Client to hub. Exact bytes for the terminal to receive, uninterpreted.</summary>
[MessagePackObject]
public sealed class SendInputRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Client to hub. Starts one bounded file upload to the attached terminal session.</summary>
[MessagePackObject]
public sealed class BeginTerminalUploadRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string UploadId { get; set; } = string.Empty;

    [Key(2)]
    public string FileName { get; set; } = string.Empty;

    [Key(3)]
    public long TotalBytes { get; set; }
}

/// <summary>Client to hub. Appends one ordered chunk to an upload the agent accepted.</summary>
[MessagePackObject]
public sealed class TerminalUploadChunkRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string UploadId { get; set; } = string.Empty;

    [Key(2)]
    public long Offset { get; set; }

    [Key(3)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Client to hub. Cancels a partial terminal upload and removes its temporary bytes.</summary>
[MessagePackObject]
public sealed class CancelTerminalUploadRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string UploadId { get; set; } = string.Empty;
}

/// <summary>Client to hub. Reflows the desk terminal too: the phone wins while attached.</summary>
[MessagePackObject]
public sealed class ResizeTerminalRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public int Cols { get; set; }

    [Key(2)]
    public int Rows { get; set; }
}

/// <summary>Client to hub. Sends 0x03 to the session.</summary>
[MessagePackObject]
public sealed class InterruptSessionRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>Client to hub. Sends a message to an attached agent-chat session.</summary>
[MessagePackObject]
public sealed class SendChatMessageRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Client to hub. Starts one bounded attachment upload for an attached agent-chat
/// session.
/// <para>
/// Deliberately not <see cref="BeginTerminalUploadRequest"/> with a different session
/// kind. The two share their transport mechanics and nothing else: a terminal upload
/// ends as a file the user pastes a path to, while this one ends as typed content
/// inside an ACP prompt and must never surface a path at all.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class BeginChatAttachmentRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string AttachmentId { get; set; } = string.Empty;

    [Key(2)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>The browser's guess. The agent re-derives the real type from the bytes.</summary>
    [Key(3)]
    public string MimeType { get; set; } = string.Empty;

    [Key(4)]
    public long TotalBytes { get; set; }
}

/// <summary>Client to hub. Appends one ordered chunk to an attachment the agent accepted.</summary>
[MessagePackObject]
public sealed class ChatAttachmentChunkRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string AttachmentId { get; set; } = string.Empty;

    [Key(2)]
    public long Offset { get; set; }

    [Key(3)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Client to hub. Removes a staged attachment, complete or not, and its bytes.</summary>
[MessagePackObject]
public sealed class CancelChatAttachmentRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string AttachmentId { get; set; } = string.Empty;
}

/// <summary>
/// Client to hub. Sends optional text plus previously staged attachments as one ACP
/// prompt.
/// <para>
/// <see cref="SendChatMessageRequest"/> is left exactly as it was and remains the
/// path a text-only prompt takes, so a phone talking to an agent that predates
/// attachments keeps working unchanged.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class SendChatPromptRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>May be empty when at least one attachment is present.</summary>
    [Key(1)]
    public string Text { get; set; } = string.Empty;

    /// <summary>Completed attachment ids, in the order the user selected them.</summary>
    [Key(2)]
    public string[] AttachmentIds { get; set; } = [];
}

/// <summary>Client to hub. Selects one option from a pending chat permission request.</summary>
[MessagePackObject]
public sealed class RespondChatPermissionRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string RequestId { get; set; } = string.Empty;

    [Key(2)]
    public string OptionId { get; set; } = string.Empty;
}

/// <summary>
/// Client to hub. Corrects the session's CLI type when detection guessed wrong.
/// <para>
/// Scoped to a session the caller is attached to, like every other client request
/// that crosses to an agent. That is not incidental: it keeps one place in the hub
/// where a client's message can reach a machine, and the picker lives on the screen
/// you are already looking at, so requiring the attachment costs the user nothing.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class SetSessionTypeRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public CliType CliType { get; set; }
}

/// <summary>
/// Client to hub. Renames a session for as long as it runs.
/// <para>
/// Unlike every other client request that names a session, this one is not routed
/// through the caller's attachment. Renaming is done from the list — that is the
/// screen the wrong name is on — and there is nothing to attach to from there. It is
/// safe because it never crosses to a machine: the hub answers it out of the caller's
/// own partition, so the worst a forged machine id can do is find nothing.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class SetSessionNameRequest
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The new name. Null or blank reverts to the name the agent gave the session,
    /// which is why the agent's name is never overwritten.
    /// </summary>
    [Key(2)]
    public string? Name { get; set; }
}

/// <summary>Client to hub. Lifts a session to the top of the list, or puts it back.</summary>
[MessagePackObject]
public sealed class SetSessionPinnedRequest
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    [Key(2)]
    public bool Pinned { get; set; }
}

/// <summary>Client to hub. Registers a Web Push subscription for awaiting-input alerts.</summary>
[MessagePackObject]
public sealed class RegisterPushRequest
{
    [Key(0)]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The subscription's <c>p256dh</c> and <c>auth</c> keys.</summary>
    [Key(1)]
    public PushKeys Keys { get; set; } = new();
}

/// <summary>Web Push subscription key material.</summary>
[MessagePackObject]
public sealed class PushKeys
{
    [Key(0)]
    public string P256dh { get; set; } = string.Empty;

    [Key(1)]
    public string Auth { get; set; } = string.Empty;
}

/// <summary>Client to hub. Lists this user's projects.</summary>
[MessagePackObject]
public sealed class ListProjectsRequest
{
}

/// <summary>Client to hub. Creates a new project.</summary>
[MessagePackObject]
public sealed class CreateProjectRequest
{
    [Key(0)]
    public string Name { get; set; } = string.Empty;

    [Key(1)]
    public string? Description { get; set; }

    [Key(2)]
    public string? SiteUrl { get; set; }

    [Key(3)]
    public string? RepoUrl { get; set; }
}

/// <summary>
/// Client to hub. Edits an existing project's fields (not its icon, which is
/// uploaded over a separate HTTP endpoint — see <c>docs/deployment.md</c>).
/// <para>
/// General's optional metadata can be edited, but its reserved name is immutable.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class UpdateProjectRequest
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
}

/// <summary>
/// Client to hub. Deletes a project. Rejected for the General project. Any
/// session still assigned to the deleted project is reassigned back to General.
/// </summary>
[MessagePackObject]
public sealed class DeleteProjectRequest
{
    [Key(0)]
    public string ProjectId { get; set; } = string.Empty;
}

/// <summary>
/// Client to hub. Moves a live session to a different project.
/// <para>
/// Not routed through the caller's attachment, for the same reason as
/// <see cref="SetSessionNameRequest"/>: moving is done from the list, and the
/// hub answers it out of the caller's own partition without ever crossing to
/// the machine.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class SetSessionProjectRequest
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Null moves the session back to General.</summary>
    [Key(2)]
    public string? ProjectId { get; set; }
}

// Hub to client (spec section 5.3).


/// <summary>Hub to client. The full picture, sent on attach of the client itself.</summary>
[MessagePackObject]
public sealed class MachineListNotification
{
    [Key(0)]
    public MachineInfo[] Machines { get; set; } = [];
}

/// <summary>Hub to client. An agent connected.</summary>
[MessagePackObject]
public sealed class MachineOnlineNotification
{
    [Key(0)]
    public MachineInfo Machine { get; set; } = new();
}

/// <summary>Hub to client. An agent disconnected; its sessions are unreachable.</summary>
[MessagePackObject]
public sealed class MachineOfflineNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;
}

/// <summary>Hub to client. A new session appeared on a machine.</summary>
[MessagePackObject]
public sealed class ClientSessionOpenedNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public SessionInfo Session { get; set; } = new();
}

/// <summary>
/// Hub to client. A session that is already on the list changed.
/// <para>
/// Sent to every client of the user, not only those attached: the type shows on the
/// session list, and a phone that is looking at the list is by definition not
/// attached to anything.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class ClientSessionUpdatedNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public SessionInfo Session { get; set; } = new();
}

/// <summary>Hub to client. A session ended.</summary>
[MessagePackObject]
public sealed class ClientSessionClosedNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    [Key(2)]
    public int ExitCode { get; set; }
}

/// <summary>Hub to client. A session is believed to be waiting on the user.</summary>
[MessagePackObject]
public sealed class ClientSessionAwaitingInputNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    [Key(2)]
    public string? Hint { get; set; }
}

/// <summary>Hub to client. Explicitly sets or clears a session's attention state.</summary>
[MessagePackObject]
public sealed class ClientSessionAttentionNotification
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string SessionId { get; set; } = string.Empty;

    [Key(2)]
    public bool AwaitingInput { get; set; }

    [Key(3)]
    public string? Hint { get; set; }
}

/// <summary>Hub to client or agent. A request failed.</summary>
[MessagePackObject]
public sealed class ErrorNotification
{
    /// <summary>One of the stable strings in <see cref="ErrorCodes"/>.</summary>
    [Key(0)]
    public string Code { get; set; } = string.Empty;

    [Key(1)]
    public string Message { get; set; } = string.Empty;

    [Key(2)]
    public string? SessionId { get; set; }
}

/// <summary>
/// Hub to client, direct RPC return of <see cref="ListProjects"/>.
/// <see cref="HubMethods.Server.ListProjects"/>.
/// </summary>
[MessagePackObject]
public sealed class ProjectListNotification
{
    [Key(0)]
    public ProjectInfo[] Projects { get; set; } = [];
}

/// <summary>
/// Hub to client, the direct RPC return of <c>CreateProject</c>/<c>UpdateProject</c>.
/// <para>
/// Carries either the created/updated project or an error, never both. Used as a
/// direct return in addition to the broadcast notifications below: the caller
/// needs the hub-generated id (or the failure) immediately, e.g. to chain an icon
/// upload, which a broadcast alone cannot give it deterministically.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class ProjectResult
{
    [Key(0)]
    public ProjectInfo? Project { get; set; }

    /// <summary>One of the stable strings in <see cref="ErrorCodes"/>, set only on failure.</summary>
    [Key(1)]
    public string? Error { get; set; }
}

/// <summary>Hub to client. Broadcast to every device of the user after a successful create.</summary>
[MessagePackObject]
public sealed class ProjectCreatedNotification
{
    [Key(0)]
    public ProjectInfo Project { get; set; } = new();
}

/// <summary>Hub to client. Broadcast to every device of the user after a successful update (including icon changes).</summary>
[MessagePackObject]
public sealed class ProjectUpdatedNotification
{
    [Key(0)]
    public ProjectInfo Project { get; set; } = new();
}

/// <summary>Hub to client. A project was deleted; its sessions have already been reassigned to General.</summary>
[MessagePackObject]
public sealed class ProjectDeletedNotification
{
    [Key(0)]
    public string ProjectId { get; set; } = string.Empty;
}
