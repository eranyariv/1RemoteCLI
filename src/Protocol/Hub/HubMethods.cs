namespace OneRemoteCli.Protocol.Hub;

/// <summary>
/// SignalR method names, shared so the agent, hub, and PWA cannot drift apart on a
/// string literal.
/// </summary>
public static class HubMethods
{
    /// <summary>Methods a peer invokes on the hub.</summary>
    public static class Server
    {
        // Agent.
        public const string RegisterMachine = "RegisterMachine";
        public const string SetMachineNotificationLevel = "SetMachineNotificationLevel";
        public const string SessionOpened = "SessionOpened";
        public const string SessionClosed = "SessionClosed";

        /// <summary>
        /// Something about a live session changed. Distinct from
        /// <see cref="SessionOpened"/> on purpose: an update is not an open, and
        /// reusing the open would mean every rename counted as a new session in the
        /// usage figures and read as one in the logs.
        /// </summary>
        public const string SessionUpdated = "SessionUpdated";

        public const string TerminalOutput = "TerminalOutput";
        public const string SessionAwaitingInput = "SessionAwaitingInput";
        public const string SessionAttention = "SessionAttention";
        public const string ChatTranscript = "ChatTranscript";

        // Client.
        public const string ClientHandshake = "ClientHandshake";
        public const string ListMachines = "ListMachines";
        public const string AttachSession = "AttachSession";
        public const string DetachSession = "DetachSession";
        public const string SendInput = "SendInput";
        public const string BeginTerminalUpload = "BeginTerminalUpload";
        public const string UploadTerminalChunk = "UploadTerminalChunk";
        public const string CancelTerminalUpload = "CancelTerminalUpload";
        public const string ResizeTerminal = "ResizeTerminal";
        public const string InterruptSession = "InterruptSession";
        public const string SendChatMessage = "SendChatMessage";
        public const string BeginChatAttachment = "BeginChatAttachment";
        public const string UploadChatAttachmentChunk = "UploadChatAttachmentChunk";
        public const string CancelChatAttachment = "CancelChatAttachment";

        /// <summary>
        /// Optional text plus staged attachments. Separate from
        /// <see cref="SendChatMessage"/> rather than an extension of it, so a phone
        /// talking to an agent that predates attachments keeps sending text the way
        /// that agent already understands.
        /// </summary>
        public const string SendChatPrompt = "SendChatPrompt";

        public const string RespondChatPermission = "RespondChatPermission";
        public const string SetSessionType = "SetSessionType";

        /// <summary>
        /// What the user calls a session, and whether it sits at the top of the list.
        /// <para>
        /// Both are answered by the hub rather than forwarded to the agent, unlike
        /// <see cref="SetSessionType"/>. The agent owns what a session *is*; the hub
        /// owns what one user chose to call it, which is why neither name nor pin
        /// appears anywhere in the agent half of this file.
        /// </para>
        /// </summary>
        public const string SetSessionName = "SetSessionName";

        /// <inheritdoc cref="SetSessionName"/>
        public const string SetSessionPinned = "SetSessionPinned";

        public const string RegisterPush = "RegisterPush";

        public const string ListProjects = "ListProjects";
        public const string CreateProject = "CreateProject";
        public const string UpdateProject = "UpdateProject";
        public const string DeleteProject = "DeleteProject";

        /// <inheritdoc cref="SetSessionName"/>
        public const string SetSessionProject = "SetSessionProject";

        // Both.
        public const string RefreshToken = "RefreshToken";
    }

    /// <summary>Methods the hub invokes on a connected agent.</summary>
    public static class Agent
    {
        public const string AttachRequested = "AttachRequested";
        public const string DetachRequested = "DetachRequested";
        public const string SendInput = "SendInput";
        public const string BeginTerminalUpload = "BeginTerminalUpload";
        public const string UploadTerminalChunk = "UploadTerminalChunk";
        public const string CancelTerminalUpload = "CancelTerminalUpload";
        public const string ResizeTerminal = "ResizeTerminal";
        public const string InterruptSession = "InterruptSession";
        public const string SendChatMessage = "SendChatMessage";
        public const string BeginChatAttachment = "BeginChatAttachment";
        public const string UploadChatAttachmentChunk = "UploadChatAttachmentChunk";
        public const string CancelChatAttachment = "CancelChatAttachment";
        public const string SendChatPrompt = "SendChatPrompt";
        public const string RespondChatPermission = "RespondChatPermission";
        public const string SetSessionTypeRequested = "SetSessionTypeRequested";
        public const string TokenExpiring = "TokenExpiring";
        public const string Error = "Error";
    }

    /// <summary>Methods the hub invokes on a connected client.</summary>
    public static class Client
    {
        public const string MachineList = "MachineList";
        public const string MachineOnline = "MachineOnline";
        public const string MachineOffline = "MachineOffline";
        public const string SessionOpened = "SessionOpened";

        /// <summary>A live session's details changed — its type, its name, or its pin.</summary>
        public const string SessionUpdated = "SessionUpdated";

        public const string SessionClosed = "SessionClosed";
        public const string TerminalOutput = "TerminalOutput";
        public const string SessionAwaitingInput = "SessionAwaitingInput";
        public const string SessionAttention = "SessionAttention";
        public const string ChatTranscript = "ChatTranscript";
        public const string TokenExpiring = "TokenExpiring";
        public const string Error = "Error";

        public const string ProjectCreated = "ProjectCreated";
        public const string ProjectUpdated = "ProjectUpdated";
        public const string ProjectDeleted = "ProjectDeleted";
    }
}
