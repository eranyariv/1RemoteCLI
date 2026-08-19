using MessagePack;

namespace OneRemoteCli.Protocol.Hub;

/// <summary>
/// Whether a <see cref="TerminalOutputNotification"/> continues the stream or
/// replaces it. A snapshot resets the client's terminal before it is applied.
/// </summary>
public enum TerminalOutputKind : byte
{
    Delta = 0,
    Snapshot = 1,
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
}
