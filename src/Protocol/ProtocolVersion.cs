namespace OneRemoteCli.Protocol;

/// <summary>
/// Wire protocol version, carried by <see cref="Hub.RegisterMachineRequest"/> and the
/// client handshake. The hub rejects anything it does not support with a clear
/// <see cref="Hub.ErrorNotification"/> rather than failing obscurely on the first
/// incompatible message.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>
    /// Version this build speaks and sends.
    /// <para>
    /// 2 added <c>SetSessionType</c> and <c>SessionUpdated</c>, and appended
    /// <see cref="Hub.SessionInfo.CliType"/>. Both are additive: a version 1 peer
    /// never invokes the new methods, and its decoder stops reading before the new
    /// field. That is why <see cref="MinimumSupported"/> did not move with it.
    /// Version 3 adds ACP-backed agent-chat sessions and their typed transcript,
    /// plus projects: <c>ListProjects</c>/<c>CreateProject</c>/<c>UpdateProject</c>/
    /// <c>DeleteProject</c>/<c>SetSessionProject</c>, their notifications, and
    /// appended <see cref="Hub.SessionInfo.ProjectId"/>. Additive for the same
    /// reason as version 2, so <see cref="MinimumSupported"/> stays put again.
    /// Version 4 adds bounded, chunked terminal file uploads. The methods are
    /// additive and older agents remain useful for every feature they already had.
    /// Version 5 appends the machine's phone-notification level to registration and
    /// adds a method for changing it live. Value zero preserves version 4 behavior,
    /// so older agents continue sending all attention events.
    /// Version 6 adds agent-chat attachments: <c>BeginChatAttachment</c>,
    /// <c>UploadChatAttachmentChunk</c>, <c>CancelChatAttachment</c> and
    /// <c>SendChatPrompt</c>, plus an appended
    /// <see cref="Hub.SessionInfo.ChatCapabilities"/> describing what the ACP agent
    /// behind a chat session accepts. Additive again: <c>SendChatMessage</c> is
    /// untouched and remains the path for text-only prompts, and a peer that omits
    /// the appended field is read as advertising no attachment support at all.
    /// Version 7 appends explicit terminal continuity state and learned project-move
    /// suggestions, and adds the optional move kind used to distinguish manual,
    /// suggested, and automatic choices. Every new field has a safe older-peer default.
    /// Version 8 appends agent-chat ownership state, allowing the PWA to wait until
    /// sequential ACP handoff succeeds instead of writing into a session another
    /// Copilot process is using.
    /// Version 9 enriches ACP plan entries with stable identity and optional hierarchy,
    /// and identifies the user turn and replacement revision for each plan snapshot.
    /// Version 10 appends a read-only snapshot of Copilot Desktop's session-local task
    /// table so the PWA can offer the same plan outside native ACP plan events.
    /// </para>
    /// </summary>
    public const int Current = 10;

    /// <summary>Oldest version this build still accepts from a peer.</summary>
    public const int MinimumSupported = 1;

    public static bool IsSupported(int version) =>
        version >= MinimumSupported && version <= Current;
}
