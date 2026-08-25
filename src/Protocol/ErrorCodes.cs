namespace OneRemoteCli.Protocol;

/// <summary>
/// Error codes carried by <see cref="Hub.ErrorNotification"/>. Stable strings rather
/// than an enum so a newer peer can send a code an older peer has never heard of
/// without the deserializer throwing.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The peer speaks a protocol version this hub cannot serve.</summary>
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";

    /// <summary>The signed-in account is not on the hub's allowlist.</summary>
    public const string AccountNotAllowed = "account_not_allowed";

    /// <summary>The named machine is not registered, or not registered to this user.</summary>
    public const string MachineNotFound = "machine_not_found";

    /// <summary>The machine is known but currently has no live agent connection.</summary>
    public const string MachineOffline = "machine_offline";

    /// <summary>The named session does not exist on that machine.</summary>
    public const string SessionNotFound = "session_not_found";

    /// <summary>The caller is not attached to the session it is trying to drive.</summary>
    public const string NotAttached = "not_attached";

    /// <summary>The access token expired mid-connection and was not refreshed in time.</summary>
    public const string TokenExpired = "token_expired";

    /// <summary>A refreshed token resolved to a different user than the one that connected.</summary>
    public const string IdentityChanged = "identity_changed";

    /// <summary>The request was malformed or failed validation.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>Unexpected server-side failure.</summary>
    public const string InternalError = "internal_error";

    /// <summary>The named project is not registered, or not registered to this user.</summary>
    public const string ProjectNotFound = "project_not_found";

    /// <summary>Another of this user's projects already has this name.</summary>
    public const string DuplicateProjectName = "duplicate_project_name";

    /// <summary>A project's optional site URL is not an absolute HTTP(S) URL.</summary>
    public const string InvalidProjectSiteUrl = "invalid_project_site_url";

    /// <summary>A project's optional repository URL is not an absolute HTTP(S) URL.</summary>
    public const string InvalidProjectRepoUrl = "invalid_project_repo_url";

    /// <summary>The reserved General project cannot be deleted.</summary>
    public const string CannotDeleteGeneralProject = "cannot_delete_general_project";

    /// <summary>A terminal attachment exceeds the shared upload limit.</summary>
    public const string FileTooLarge = "file_too_large";

    /// <summary>The named upload is missing, stale, or belongs to another attachment.</summary>
    public const string UploadNotFound = "upload_not_found";

    /// <summary>The agent could not safely persist the uploaded bytes.</summary>
    public const string UploadFailed = "upload_failed";

    /// <summary>The upload was deliberately cancelled before completion.</summary>
    public const string UploadCancelled = "upload_cancelled";

    /// <summary>The connected agent predates terminal file uploads.</summary>
    public const string UploadUnavailable = "upload_unavailable";
}
