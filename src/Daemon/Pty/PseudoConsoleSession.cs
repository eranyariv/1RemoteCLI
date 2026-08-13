using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OneRemoteCli.Daemon.Pty;

/// <summary>
/// A live pseudoconsole with a child process attached.
/// <para>
/// Windows cannot retroactively attach a pseudoconsole to an already-running
/// process, so every remotable session must be born here, under the wrapper. The
/// session's lifetime is therefore the wrapper's lifetime: closing the desk terminal
/// ends it, which is why there is never orphaned state to reconcile.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PseudoConsoleSession : IAsyncDisposable
{
    /// <summary>
    /// Gives conhost a moment to flush before the pseudoconsole goes away.
    /// <para>
    /// There is no reliable signal for "conhost has finished painting". Peeking the
    /// output pipe is not an option: on an anonymous pipe a peek blocks behind the
    /// reader's pending read, so it deadlocks against the very consumer it is meant
    /// to observe. A short fixed grace is the pragmatic alternative, and it only ever
    /// delays teardown of an already-dead session.
    /// </para>
    /// </summary>
    private const int FlushGraceMs = 250;

    private readonly SafePseudoConsoleHandle _pty;
    private readonly FileStream _input;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _ptyLock = new();

    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEvent _processWait;
    private readonly RegisteredWaitHandle _registeredWait;

    private bool _ptyClosed;
    private int _disposed;

    private PseudoConsoleSession(
        SafePseudoConsoleHandle pty,
        SafeFileHandle ptyInputWrite,
        SafeFileHandle ptyOutputRead,
        IntPtr processHandle,
        IntPtr threadHandle,
        int processId,
        int cols,
        int rows)
    {
        _pty = pty;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
        Cols = cols;
        Rows = rows;

        // One stream for the lifetime of the session. Wrapping the handle per write
        // would close it on dispose, so only the first write would ever land.
        _input = new FileStream(ptyInputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
        Output = new FileStream(ptyOutputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);

        // ConPTY holds the write end of the output pipe for the lifetime of the
        // *pseudoconsole*, not the child, so the reader never sees EOF just because
        // the child exited. Watch the process and close the pseudoconsole when it
        // goes, which drops that last writer and lets Output complete.
        _processWait = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false),
        };

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _processWait,
            static (state, _) => ((PseudoConsoleSession)state!).OnChildExited(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Process id of the child hosted under the pseudoconsole.</summary>
    public int ProcessId { get; }

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    /// <summary>
    /// Raw VT byte stream produced by the child. Reaches end of file once the child
    /// has exited and the pseudoconsole has flushed everything it buffered.
    /// </summary>
    public Stream Output { get; }

    /// <summary>Completes with the child's exit code.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>
    /// Creates a pseudoconsole and launches <paramref name="commandLine"/> attached to it.
    /// </summary>
    /// <param name="commandLine">Full command line, already quoted as the child expects.</param>
    /// <param name="workingDirectory">Working directory, or null to inherit.</param>
    /// <param name="cols">Initial column count.</param>
    /// <param name="rows">Initial row count.</param>
    public static PseudoConsoleSession Start(
        string commandLine,
        string? workingDirectory,
        int cols,
        int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ValidateSize(cols, rows);

        // Two anonymous pipes. We keep the ends we drive; the pseudoconsole keeps the
        // ends it drives. Our ends must not be inheritable or the child would hold
        // them open and the output stream would never see EOF.
        if (!NativeMethods.CreatePipe(out SafeFileHandle ptyInputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
        {
            NativeMethods.ThrowLastError("CreatePipe (input)");
        }

        if (!NativeMethods.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle ptyOutputWrite, IntPtr.Zero, 0))
        {
            ptyInputRead.Dispose();
            inputWrite.Dispose();
            NativeMethods.ThrowLastError("CreatePipe (output)");
        }

        NativeMethods.SetHandleInformation(inputWrite, NativeMethods.HANDLE_FLAG_INHERIT, 0);
        NativeMethods.SetHandleInformation(outputRead, NativeMethods.HANDLE_FLAG_INHERIT, 0);

        SafePseudoConsoleHandle? pty = null;
        SafeProcThreadAttributeList? attributes = null;

        try
        {
            var size = new NativeMethods.COORD { X = (short)cols, Y = (short)rows };
            int hr = NativeMethods.CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out IntPtr hpc);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            pty = new SafePseudoConsoleHandle(hpc);

            // The pseudoconsole duplicated the ends it needs, so drop ours now. Any
            // stray copy of the output pipe's write end would keep the reader blocked
            // even after the pseudoconsole is closed.
            ptyInputRead.Dispose();
            ptyOutputWrite.Dispose();

            attributes = SafeProcThreadAttributeList.CreateForPseudoConsole(pty);

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),

                    // Say "use these standard handles" and then hand over none. Without
                    // this, the child silently inherits the *values* of our standard
                    // handles, and when this process was itself launched with redirected
                    // stdio (a test host, a service, anything non-interactive) those are
                    // pipe handles that do not exist in the child. The child then writes
                    // into a dead handle: it runs, it attaches to the pseudoconsole, it
                    // even sets the window title, but nothing it prints ever reaches the
                    // screen buffer. Nulling them lets the console host install the
                    // pseudoconsole's own handles instead.
                    dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero,
                },
                lpAttributeList = attributes.DangerousGetHandle(),
            };

            bool created = NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out NativeMethods.PROCESS_INFORMATION processInfo);

            if (!created)
            {
                NativeMethods.ThrowLastError($"CreateProcess ({commandLine})");
            }

            attributes.Dispose();
            attributes = null;

            return new PseudoConsoleSession(
                pty,
                inputWrite,
                outputRead,
                processInfo.hProcess,
                processInfo.hThread,
                processInfo.dwProcessId,
                cols,
                rows);
        }
        catch
        {
            attributes?.Dispose();
            pty?.Dispose();
            ptyInputRead.Dispose();
            ptyOutputWrite.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes bytes straight into the pseudoconsole. Nothing is interpreted, so remote
    /// input is indistinguishable from a local keypress.
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (data.IsEmpty)
        {
            return;
        }

        // Serialised because concurrent writes to the same pipe can interleave
        // part-way through a multi-byte escape sequence.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Convenience overload for text input.</summary>
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default) =>
        WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Sends <c>0x03</c>, the same byte the keyboard produces for Ctrl+C.</summary>
    public ValueTask InterruptAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(new byte[] { 0x03 }, cancellationToken);

    /// <summary>
    /// Reflows the pseudoconsole. The hosted program sees an ordinary terminal resize
    /// and redraws itself, which is what makes the phone-wins resize policy work.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateSize(cols, rows);

        if (cols == Cols && rows == Rows)
        {
            return;
        }

        lock (_ptyLock)
        {
            // The child may have exited between the check and here, which closes the
            // pseudoconsole from the watcher thread. Resizing a dead terminal is a
            // no-op, not an error.
            if (_ptyClosed)
            {
                return;
            }

            int hr = NativeMethods.ResizePseudoConsole(
                _pty.DangerousGetHandle(),
                new NativeMethods.COORD { X = (short)cols, Y = (short)rows });

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        Cols = cols;
        Rows = rows;
    }

    /// <summary>Exit code of the child, or null while it is still running.</summary>
    public int? TryGetExitCode()
    {
        const int StillActive = 259;

        if (!NativeMethods.GetExitCodeProcess(_processHandle, out int code))
        {
            return null;
        }

        return code == StillActive ? null : code;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registeredWait.Unregister(null);
        _processWait.Dispose();

        ClosePty();

        await Output.DisposeAsync().ConfigureAwait(false);
        await _input.DisposeAsync().ConfigureAwait(false);

        if (_threadHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_threadHandle);
        }

        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
        }

        _writeLock.Dispose();
        _exited.TrySetResult(TryGetExitCode() ?? -1);
    }

    private void OnChildExited()
    {
        // Closing the pseudoconsole is what releases the last writer on the output
        // pipe, so it is the only way Output ever reaches EOF. Wait a beat first, or a
        // child that writes and exits immediately loses its final frame.
        Thread.Sleep(FlushGraceMs);

        ClosePty();
        _exited.TrySetResult(TryGetExitCode() ?? -1);
    }

    private void ClosePty()
    {
        lock (_ptyLock)
        {
            if (_ptyClosed)
            {
                return;
            }

            _ptyClosed = true;
            _pty.Dispose();
        }
    }

    private static void ValidateSize(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);
    }
}
