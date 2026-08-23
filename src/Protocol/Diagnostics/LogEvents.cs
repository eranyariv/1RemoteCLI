using Microsoft.Extensions.Logging;

namespace OneRemoteCli.Protocol.Diagnostics;

/// <summary>
/// Every log event this system can produce, and the only way to produce one.
/// <para>
/// The product's central promise is that what you type into a terminal reaches your
/// phone and goes nowhere else: the hub relays bytes and never persists them, and
/// screen state lives only in agent memory (spec §7.3). One well-meaning
/// <c>logger.LogDebug("received: {Data}", data)</c> added during a late-night debugging
/// session destroys that promise permanently and invisibly, writing API keys, .env
/// contents and private source to disk and to Azure.
/// </para>
/// <para>
/// Discipline does not survive that; a closed vocabulary does. These are compile-time
/// message templates with fixed parameter lists, and no member here accepts a byte
/// array, a span, a screen or a line of text. There is deliberately no
/// <c>Log(string message)</c> member, because a free-form string parameter is exactly
/// the hole this is closing.
/// </para>
/// <para>
/// What is here is what actually needs diagnosing: who connected, who was refused and
/// why, what registered, what was dropped, and what threw. Sizes and sequence numbers
/// rather than content — which is what you need in order to debug framing and flow
/// control anyway, since the bytes themselves tell you nothing that <c>seq</c> and a
/// length do not.
/// </para>
/// </summary>
public static partial class LogEvents
{
    // 1000s: connection lifecycle.

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Connected to the hub as machine {MachineId}.")]
    public static partial void HubConnected(this ILogger logger, string machineId);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Disconnected from the hub ({Reason}).")]
    public static partial void HubDisconnected(this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Reconnecting to the hub in {DelaySeconds}s.")]
    public static partial void HubReconnecting(this ILogger logger, double delaySeconds);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "The hub refused this machine: {Code} - {Reason}")]
    public static partial void HubRefused(this ILogger logger, string code, string reason);

    // 1100s: authentication. Outcomes and reasons, never tokens: a bearer token in a
    // log is a credential in a log, and a log file outlives the token's lifetime.

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Signed in as {Account}.")]
    public static partial void SignedIn(this ILogger logger, string account);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Not signed in, so this machine is not reachable. {Reason}")]
    public static partial void NotSignedIn(this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Could not renew the access token: {Code}")]
    public static partial void TokenRenewalFailed(this ILogger logger, string code);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Rejected a caller: {Reason}")]
    public static partial void AuthorizationRejected(this ILogger logger, string reason);

    // 1200s: registration.

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Machine {MachineId} registered.")]
    public static partial void MachineRegistered(this ILogger logger, string machineId);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "Machine {MachineId} went offline.")]
    public static partial void MachineOffline(this ILogger logger, string machineId);

    /// <remarks>
    /// The program name is logged; the session's display name is not. The program is
    /// metadata we chose to record, whereas the display name is a string the user
    /// typed, and someone who pastes something sensitive into <c>--name</c> should not
    /// find it on disk.
    /// </remarks>
    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "Session {SessionId} on machine {MachineId} opened, running {Program}.")]
    public static partial void SessionOpened(this ILogger logger, string machineId, string sessionId, string program);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Information,
        Message = "Session {SessionId} on machine {MachineId} closed with exit code {ExitCode}.")]
    public static partial void SessionClosed(this ILogger logger, string machineId, string sessionId, int exitCode);

    // 1300s: attach and relay.

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "A client attached to session {SessionId} from seq {FromSeq}; answered with {Answer}.")]
    public static partial void ClientAttached(this ILogger logger, string sessionId, long fromSeq, string answer);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "A client detached from session {SessionId}.")]
    public static partial void ClientDetached(this ILogger logger, string sessionId);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Debug,
        Message = "Relayed {Bytes} bytes as seq {Seq} for session {SessionId}.")]
    public static partial void OutputRelayed(this ILogger logger, string sessionId, long seq, int bytes);

    /// <remarks>
    /// The one event whose absence would make a whole class of bug undiagnosable: a
    /// client that silently falls behind looks exactly like a session that stopped
    /// producing output.
    /// </remarks>
    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Warning,
        Message = "Dropped {Bytes} bytes for session {SessionId} ({Reason}); the client will be resynchronised.")]
    public static partial void OutputDropped(this ILogger logger, string sessionId, int bytes, string reason);

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Debug,
        Message = "Delivered {Bytes} bytes of input to session {SessionId}.")]
    public static partial void InputDelivered(this ILogger logger, string sessionId, int bytes);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Information,
        Message = "Session {SessionId} is waiting for input.")]
    public static partial void SessionAwaitingInput(this ILogger logger, string sessionId);

    // 1400s: the local pipe.

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "Listening on pipe {PipeName}.")]
    public static partial void PipeListening(this ILogger logger, string pipeName);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "A wrapper connected to the agent.")]
    public static partial void WrapperConnected(this ILogger logger);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Information,
        Message = "A wrapper disconnected ({Reason}).")]
    public static partial void WrapperDisconnected(this ILogger logger, string reason);

    // 1500s: keeping the agent up to date.

    /// <remarks>
    /// Everything the update service has to say goes through here, at Information,
    /// because the file log is the only record on a machine whose agent runs hidden from
    /// a scheduled task — there is no console for it to write to, and a machine that
    /// silently never updates is exactly the thing this needs to be diagnosable.
    /// </remarks>
    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Information,
        Message = "{Detail}")]
    public static partial void Update(this ILogger logger, string detail);

    // 1600s: desktop settings actions.

    /// <remarks>
    /// Kept as one free-form diagnostic event because this traces a short native call
    /// sequence whose useful fields depend on how far Windows got. Callers must not put
    /// shortcut paths, targets, arguments, or display names in <paramref name="detail"/>.
    /// </remarks>
    [LoggerMessage(
        EventId = 1600,
        Level = LogLevel.Information,
        Message = "Shortcut picker: {Detail}")]
    public static partial void ShortcutPicker(this ILogger logger, string detail);

    // 1900s: things that went wrong.

    /// <remarks>
    /// Takes the exception rather than a message, so the stack trace survives and the
    /// call site has nothing to interpolate into. <paramref name="operation"/> is a
    /// fixed phrase naming what was being attempted, not a formatted sentence.
    /// </remarks>
    [LoggerMessage(
        EventId = 1900,
        Level = LogLevel.Error,
        Message = "{Operation} failed.")]
    public static partial void Failed(this ILogger logger, Exception exception, string operation);

    [LoggerMessage(
        EventId = 1901,
        Level = LogLevel.Warning,
        Message = "{Operation} was refused: {Code} - {Reason}")]
    public static partial void Refused(this ILogger logger, string operation, string code, string reason);
}
