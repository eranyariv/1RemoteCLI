namespace OneRemoteCli.Protocol;

/// <summary>
/// Bounds shared by the browser, hub, and agent for agent-chat attachments.
/// <para>
/// Deliberately much smaller than <see cref="TerminalUploadLimits"/>, and for a
/// different reason. A terminal attachment becomes a file on disk, so 25 MB costs
/// disk. A chat attachment becomes Base64 inside one ACP <c>session/prompt</c> JSON
/// line and then part of a model context: the bytes are inflated by four thirds on
/// the way in, held in memory by both processes at once, and charged against a
/// context window that is far smaller than any disk. The aggregate ceiling below is
/// therefore the number that matters — <see cref="MaxPromptBytes"/> of raw bytes is
/// roughly <c>13.4 MB</c> of Base64 on a single line.
/// </para>
/// </summary>
public static class ChatAttachmentLimits
{
    /// <summary>Comfortably above a full-resolution phone photo, well below a video.</summary>
    public const long MaxAttachmentBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Every attachment on one prompt, added up. Base64 expands this by four thirds
    /// before it reaches the ACP agent, which is why it is not simply the per-file
    /// limit multiplied by <see cref="MaxAttachmentCount"/>.
    /// </summary>
    public const long MaxPromptBytes = 10 * 1024 * 1024;

    /// <summary>Small on purpose: a phone composer is not a file manager.</summary>
    public const int MaxAttachmentCount = 4;

    /// <summary>Same chunk size as terminal uploads, for the same SignalR reason.</summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>Original names are display hints; the agent sanitizes them before using them on disk.</summary>
    public const int MaxFileNameChars = 240;

    /// <summary>Long enough for any real media type, short enough to reject a smuggled payload.</summary>
    public const int MaxMimeTypeChars = 128;

    /// <summary>Unchanged from the text-only era, so an existing prompt still fits.</summary>
    public const int MaxPromptTextChars = 20_000;
}
