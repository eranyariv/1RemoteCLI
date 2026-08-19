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

        // Client.
        public const string ClientHandshake = "ClientHandshake";
        public const string ListMachines = "ListMachines";
        public const string AttachSession = "AttachSession";
        public const string DetachSession = "DetachSession";
        public const string SendInput = "SendInput";
        public const string ResizeTerminal = "ResizeTerminal";
        public const string InterruptSession = "InterruptSession";
        public const string SetSessionType = "SetSessionType";
        public const string RegisterPush = "RegisterPush";

        // Both.
        public const string RefreshToken = "RefreshToken";
    }

    /// <summary>Methods the hub invokes on a connected agent.</summary>
    public static class Agent
    {
        public const string AttachRequested = "AttachRequested";
        public const string DetachRequested = "DetachRequested";
        public const string SendInput = "SendInput";
        public const string ResizeTerminal = "ResizeTerminal";
        public const string InterruptSession = "InterruptSession";
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

        /// <summary>A live session's details changed — its type, and later its name.</summary>
        public const string SessionUpdated = "SessionUpdated";

        public const string SessionClosed = "SessionClosed";
        public const string TerminalOutput = "TerminalOutput";
        public const string SessionAwaitingInput = "SessionAwaitingInput";
        public const string TokenExpiring = "TokenExpiring";
        public const string Error = "Error";
    }
}
