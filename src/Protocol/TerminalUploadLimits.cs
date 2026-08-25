namespace OneRemoteCli.Protocol;

/// <summary>Bounds shared by the browser, hub, and agent for terminal file uploads.</summary>
public static class TerminalUploadLimits
{
    /// <summary>Large enough for source archives and phone photos without turning the relay into storage.</summary>
    public const long MaxFileBytes = 25 * 1024 * 1024;

    /// <summary>Stays comfortably below SignalR's receive ceiling while keeping round trips reasonable.</summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>Original names are display hints only; the agent sanitizes them before using them on disk.</summary>
    public const int MaxFileNameChars = 240;
}
