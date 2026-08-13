using System.Runtime.Versioning;
using System.Threading.Channels;
using OneRemoteCli.Daemon.Pty;

namespace OneRemoteCli.Daemon.Wrapper;

/// <summary>
/// The wrapper's steady state: a tee in both directions between the pseudoconsole,
/// the desk terminal and the agent.
/// <para>
/// <code>
///    PTY output --+--&gt; local console   (the desk experience is unchanged)
///                 +--&gt; agent           (and so the phone sees the same bytes)
///
///    local stdin ----&gt; PTY input
///    agent input ----&gt; PTY input
/// </code>
/// Nothing here parses VT. The wrapper moves bytes; interpretation belongs to the
/// agent's emulator and to the phone's renderer, so there is exactly one screen
/// model in the system rather than three that can disagree.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WrapperSession
{
    private readonly PseudoConsoleSession _pty;
    private readonly ILocalTerminal _terminal;
    private readonly IAgentConnection _agent;
    private readonly Action<string> _warn;

    /// <param name="warn">
    /// Where to report a broken agent link. The session keeps running locally when
    /// that happens — killing a user's shell because the phone link died would be a
    /// worse outcome than losing remote access — but it must never be silent.
    /// </param>
    public WrapperSession(
        PseudoConsoleSession pty,
        ILocalTerminal terminal,
        IAgentConnection agent,
        Action<string>? warn = null)
    {
        _pty = pty;
        _terminal = terminal;
        _agent = agent;
        _warn = warn ?? (_ => { });
    }

    /// <summary>
    /// Pumps until the child exits. Returns the child's exit code so the wrapper can
    /// exit with it and compose correctly in scripts.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task commands = PumpAgentCommandsAsync(stopping.Token);
        StartLocalInputPump(stopping.Token);

        // The output pump owns the session's lifetime: it ends at EOF, which the
        // pseudoconsole only reaches once the child has exited and conhost has
        // flushed. Waiting on the process alone would race that final frame.
        await PumpOutputAsync(stopping.Token).ConfigureAwait(false);

        int exitCode = await _pty.Exited.ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);
        await SafelyAsync(() => _agent.CloseSessionAsync(exitCode, CancellationToken.None)).ConfigureAwait(false);
        await Swallow(commands).ConfigureAwait(false);

        return exitCode;
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _pty.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The pseudoconsole went away underneath us; that is the same as EOF.
                return;
            }

            if (read == 0)
            {
                return;
            }

            var chunk = buffer.AsMemory(0, read);

            // Desk first. The local terminal is the experience the user is actually
            // looking at, and it must not wait on a remote link.
            await _terminal.Output.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            await _terminal.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _agent.SendOutputAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _warn($"1remote: lost the agent connection ({ex.Message}). This session is no longer shareable.");
            }
        }
    }

    /// <summary>
    /// Reads the desk keyboard on a dedicated thread.
    /// <para>
    /// A blocking console read cannot be cancelled, so this thread is deliberately a
    /// background thread and is simply abandoned at shutdown rather than awaited.
    /// Joining it would mean waiting for a keypress the user has no reason to make.
    /// </para>
    /// </summary>
    private void StartLocalInputPump(CancellationToken cancellationToken)
    {
        var thread = new Thread(() =>
        {
            byte[] buffer = new byte[1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = _terminal.Input.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        return;
                    }

                    // PseudoConsoleSession serialises writes, so a burst from the
                    // keyboard can never land inside a sequence sent by the phone.
                    _pty.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (Exception)
            {
                // Torn down mid-read, or the PTY closed first. Either way the session
                // is ending and there is nothing useful left to do on this thread.
            }
        })
        {
            IsBackground = true,
            Name = "1remote local input",
        };

        thread.Start();
    }

    private async Task PumpAgentCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AgentCommand command in _agent.Commands.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (command)
                {
                    case AgentCommand.Input input:
                        await _pty.WriteAsync(input.Bytes, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentCommand.Resize resize:
                        _pty.Resize(resize.Cols, resize.Rows);
                        break;

                    case AgentCommand.Interrupt:
                        await _pty.InterruptAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _warn($"1remote: stopped accepting remote input ({ex.Message}).");
        }
    }

    private static async Task SafelyAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown is best-effort: the child has already exited and the user's
            // exit code matters more than a farewell the agent may never read.
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
