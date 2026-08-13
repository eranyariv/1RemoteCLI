using MessagePack;

namespace OneRemoteCli.Protocol.Pipe;

/// <summary>
/// Discriminator for the length-prefixed MessagePack frames exchanged between the
/// wrapper and the agent over the named pipe (spec section 5.1).
/// </summary>
public enum PipeMessageKind : byte
{
    /// <summary>Wrapper to agent: a PTY-hosted child has started.</summary>
    SessionOpened = 1,

    /// <summary>Agent to wrapper: the session was registered and given an id.</summary>
    SessionAccepted = 2,

    /// <summary>Wrapper to agent: raw PTY output bytes.</summary>
    Output = 3,

    /// <summary>Wrapper to agent: the child exited.</summary>
    SessionClosed = 4,

    /// <summary>Agent to wrapper: bytes to write into the PTY.</summary>
    Input = 5,

    /// <summary>Agent to wrapper: resize the pseudoconsole.</summary>
    Resize = 6,

    /// <summary>Agent to wrapper: send 0x03 to the PTY.</summary>
    Interrupt = 7,
}

/// <summary>
/// Envelope for every pipe frame. The kind is read first so a peer can skip a
/// payload it does not understand instead of desynchronising the stream.
/// </summary>
[MessagePackObject]
public sealed class PipeEnvelope
{
    [Key(0)]
    public PipeMessageKind Kind { get; set; }

    /// <summary>MessagePack-encoded body whose type is determined by <see cref="Kind"/>.</summary>
    [Key(1)]
    public byte[] Payload { get; set; } = [];
}

/// <summary>Wrapper to agent. Announces a newly started PTY-hosted child.</summary>
[MessagePackObject]
public sealed class SessionOpenedMessage
{
    [Key(0)]
    public string Program { get; set; } = string.Empty;

    [Key(1)]
    public string[] Args { get; set; } = [];

    [Key(2)]
    public string Cwd { get; set; } = string.Empty;

    [Key(3)]
    public int Cols { get; set; }

    [Key(4)]
    public int Rows { get; set; }

    /// <summary>Optional friendly label; the agent falls back to the program name.</summary>
    [Key(5)]
    public string? DisplayName { get; set; }
}

/// <summary>Agent to wrapper. Confirms registration and assigns the session id.</summary>
[MessagePackObject]
public sealed class SessionAcceptedMessage
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>Wrapper to agent. Raw PTY output, exactly as the child emitted it.</summary>
[MessagePackObject]
public sealed class OutputMessage
{
    [Key(0)]
    public byte[] Bytes { get; set; } = [];
}

/// <summary>Wrapper to agent. The child exited; the session is over.</summary>
[MessagePackObject]
public sealed class SessionClosedMessage
{
    [Key(0)]
    public int ExitCode { get; set; }
}

/// <summary>Agent to wrapper. Bytes to write into the PTY, uninterpreted.</summary>
[MessagePackObject]
public sealed class InputMessage
{
    [Key(0)]
    public byte[] Bytes { get; set; } = [];
}

/// <summary>Agent to wrapper. Resize the pseudoconsole.</summary>
[MessagePackObject]
public sealed class ResizeMessage
{
    [Key(0)]
    public int Cols { get; set; }

    [Key(1)]
    public int Rows { get; set; }
}

/// <summary>
/// Agent to wrapper. Carries no fields; the wrapper writes 0x03 to the PTY.
/// Modelled as a type anyway so every frame decodes uniformly.
/// </summary>
[MessagePackObject]
public sealed class InterruptMessage
{
}
