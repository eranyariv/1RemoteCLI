using MessagePack;

namespace OneRemoteCli.Protocol.Hub;

// Agent to hub (spec section 5.2).

/// <summary>First call an agent makes after connecting. Carries the protocol version.</summary>
[MessagePackObject]
public sealed class RegisterMachineRequest
{
    [Key(0)]
    public string MachineId { get; set; } = string.Empty;

    [Key(1)]
    public string DisplayName { get; set; } = string.Empty;

    [Key(2)]
    public string Os { get; set; } = string.Empty;

    [Key(3)]
    public string AgentVersion { get; set; } = string.Empty;

    [Key(4)]
    public int ProtocolVersion { get; set; }
}

/// <summary>Agent to hub. A wrapper started a new session on this machine.</summary>
[MessagePackObject]
public sealed class AgentSessionOpenedNotification
{
    [Key(0)]
    public SessionInfo Session { get; set; } = new();
}

/// <summary>Agent to hub. A session ended, either because the child exited or the wrapper died.</summary>
[MessagePackObject]
public sealed class AgentSessionClosedNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public int ExitCode { get; set; }
}

/// <summary>
/// Agent to hub. Terminal bytes for attached clients.
/// <para>
/// <see cref="Data"/> is treated as opaque by the hub so end-to-end encryption can be
/// added later without changing any message shape.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class TerminalOutputNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Monotonic per-session sequence number, used for delta-or-snapshot resume.</summary>
    [Key(1)]
    public long Seq { get; set; }

    [Key(2)]
    public TerminalOutputKind Kind { get; set; }

    [Key(3)]
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// When set, only this client connection receives the frame.
    /// <para>
    /// Live output is for everyone watching a session, but a repaint is an answer to
    /// one client's question — it attached, or it fell behind. Broadcasting it would
    /// redraw screens that were already correct, and broadcasting a resume replay would
    /// apply output a second time to a client that never missed it.
    /// </para>
    /// <para>
    /// Targeted frames carry the session's current sequence number rather than
    /// consuming a new one, so they leave no hole in the shared stream for the clients
    /// that never receive them.
    /// </para>
    /// </summary>
    [Key(4)]
    public string? TargetConnectionId { get; set; }
}

/// <summary>Agent to hub. The idle heuristic thinks this session is waiting on the user.</summary>
[MessagePackObject]
public sealed class SessionAwaitingInputNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Short human-readable reason, for the push notification body.</summary>
    [Key(1)]
    public string? Hint { get; set; }
}

/// <summary>
/// Agent or client to hub. Supplies a fresh access token for a live connection.
/// <para>
/// SignalR does not re-authenticate after the handshake, so without this a socket
/// would outlive its token indefinitely. The hub asserts the refreshed token
/// resolves to the same <c>UserKey</c> and aborts the connection if it does not.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class RefreshTokenRequest
{
    [Key(0)]
    public string Token { get; set; } = string.Empty;
}

// Hub to agent (spec section 5.2).

/// <summary>Hub to agent. A client wants to attach; send it a snapshot or a delta from <see cref="LastSeq"/>.</summary>
[MessagePackObject]
public sealed class AttachRequestedNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string ClientConnectionId { get; set; } = string.Empty;

    [Key(2)]
    public int Cols { get; set; }

    [Key(3)]
    public int Rows { get; set; }

    /// <summary>Last sequence the client saw. Null, or too old for the tail buffer, forces a snapshot.</summary>
    [Key(4)]
    public long? LastSeq { get; set; }
}

/// <summary>Hub to agent. A client detached or disconnected.</summary>
[MessagePackObject]
public sealed class DetachRequestedNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string ClientConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Hub to agent. Bytes to write into the PTY. Pure passthrough: the hub never
/// interprets <see cref="Data"/>, which keeps phone input indistinguishable from
/// keyboard input.
/// </summary>
[MessagePackObject]
public sealed class SendInputNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Hub to agent. Resize the session's pseudoconsole. The phone wins while attached.</summary>
[MessagePackObject]
public sealed class ResizeTerminalNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public int Cols { get; set; }

    [Key(2)]
    public int Rows { get; set; }
}

/// <summary>Hub to agent. Send 0x03 to the session.</summary>
[MessagePackObject]
public sealed class InterruptSessionNotification
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// Hub to agent or client. The presented token is close to expiry; send a
/// <see cref="RefreshTokenRequest"/> before <see cref="ExpiresAt"/> or the
/// connection is aborted.
/// </summary>
[MessagePackObject]
public sealed class TokenExpiringNotification
{
    [Key(0)]
    public DateTimeOffset ExpiresAt { get; set; }
}
