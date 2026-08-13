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
