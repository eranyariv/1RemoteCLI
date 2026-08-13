namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>
/// The terminal the wrapper itself is running in — the one at the desk.
/// <para>
/// An interface rather than direct <c>Console</c> calls so the tee can be tested
/// against in-memory streams. Every real implementation must restore whatever
/// console mode it changed: leaving a shell in raw mode outlives the process and
/// leaves the user with a terminal that no longer echoes what they type.
/// </para>
/// </summary>
public interface ILocalTerminal : IDisposable
{
    /// <summary>Visible width in columns, used to size the pseudoconsole.</summary>
    int Cols { get; }

    /// <summary>Visible height in rows.</summary>
    int Rows { get; }

    /// <summary>Keystrokes from the user at the desk.</summary>
    Stream Input { get; }

    /// <summary>Where the child's output is painted.</summary>
    Stream Output { get; }
}
