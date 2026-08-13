using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OneRemoteCli.Daemon.Pty;

/// <summary>
/// Owns an <c>HPCON</c> returned by <c>CreatePseudoConsole</c>.
/// <para>
/// A dedicated SafeHandle rather than a raw IntPtr so the pseudoconsole is released
/// even if the wrapper faults between creating it and starting the child. Note that
/// <c>ClosePseudoConsole</c> blocks until the attached client has drained, which is
/// why the output pipe must be read until EOF before disposing.
/// </para>
/// </summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafePseudoConsoleHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafePseudoConsoleHandle(IntPtr existing)
        : base(ownsHandle: true)
    {
        SetHandle(existing);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}

/// <summary>
/// Owns the unmanaged <c>PROC_THREAD_ATTRIBUTE_LIST</c> that binds a child process
/// to a pseudoconsole. The list must outlive the <c>CreateProcess</c> call, so it is
/// a handle rather than a stack buffer.
/// </summary>
internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcThreadAttributeList()
        : base(ownsHandle: true)
    {
    }

    /// <summary>
    /// Allocates a one-entry list and sets the pseudoconsole attribute.
    /// </summary>
    internal static SafeProcThreadAttributeList CreateForPseudoConsole(SafePseudoConsoleHandle pty)
    {
        nint size = 0;

        // First call always fails with ERROR_INSUFFICIENT_BUFFER and reports the size.
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0)
        {
            NativeMethods.ThrowLastError("InitializeProcThreadAttributeList (sizing)");
        }

        var list = new SafeProcThreadAttributeList();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        list.SetHandle(buffer);

        if (!NativeMethods.InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
        {
            list.Dispose();
            NativeMethods.ThrowLastError("InitializeProcThreadAttributeList");
        }

        list._initialized = true;

        // The HPCON goes in by value, not by address: unlike most attributes, the
        // pseudoconsole attribute's lpValue *is* the handle. Passing a pointer to it
        // instead is accepted silently and produces a child with no console at all.
        if (!NativeMethods.UpdateProcThreadAttribute(
                buffer,
                0,
                NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                pty.DangerousGetHandle(),
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            NativeMethods.ThrowLastError("UpdateProcThreadAttribute");
        }

        return list;
    }

    private bool _initialized;

    protected override bool ReleaseHandle()
    {
        if (_initialized)
        {
            NativeMethods.DeleteProcThreadAttributeList(handle);
        }

        Marshal.FreeHGlobal(handle);
        return true;
    }
}
