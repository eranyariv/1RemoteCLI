using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Shell;

/// <summary>
/// What a <c>.lnk</c> carries, as a value.
/// <para>
/// Only the fields that decide what happens when the file is double-clicked, plus
/// the icon, which decides whether the user recognises it. A shortcut holds a good
/// deal more — hotkeys, link-tracking data, an id list — and none of it survives a
/// copy any better than it survives being edited by hand.
/// </para>
/// </summary>
/// <param name="Target">
/// The program, with environment variables already expanded. A <c>.lnk</c> stores
/// them unexpanded, and <c>CreateProcess</c> does not expand anything, so a target
/// read straight out of the file would be handed to the pseudoconsole verbatim and
/// fail as "the system cannot find the file specified".
/// </param>
/// <param name="Arguments">
/// The child's command line, as one already-quoted string. Kept as the shell keeps
/// it rather than split: splitting and rejoining is two chances to change what the
/// child is asked to do, and the string is being copied, not interpreted.
/// </param>
/// <param name="WorkingDirectory">Where the program starts. Often the only reason a tool works.</param>
/// <param name="IconPath">The file the icon comes from, empty when the shell derives it from the target.</param>
/// <param name="IconIndex">Which icon in that file.</param>
/// <param name="Description">The shortcut's comment, shown as its tooltip in Explorer.</param>
/// <param name="RunAsAdministrator">
/// Whether the shortcut asks for elevation. Load-bearing rather than informational:
/// the agent is per-user and unelevated, and an elevated child cannot reach its pipe.
/// </param>
public readonly record struct ShellLinkInfo(
    string Target,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconPath = "",
    int IconIndex = 0,
    string Description = "",
    bool RunAsAdministrator = false)
{
    /// <summary>
    /// Whether this shortcut names a program at all.
    /// <para>
    /// Store and MSIX shortcuts carry an AppUserModelID and nothing else: there is no
    /// file to hand to a pseudoconsole, and the shell reports that by returning an
    /// empty path rather than by failing.
    /// </para>
    /// </summary>
    public bool HasProgram => !string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// Reading and writing Windows shortcuts.
/// <para>
/// A <c>.lnk</c> is a structured binary format with no managed writer, so this goes
/// through the same COM interface Explorer itself uses. The alternative — hand-rolling
/// the format — produces files that work until Windows tightens something.
/// </para>
/// <para>
/// It lives here rather than in <c>Install/</c>, where it started, because there are
/// now two callers with nothing else in common: the installer writes the Start Menu
/// entries, and shortcut wrapping reads a shortcut the user already has and writes a
/// second one beside it. A feature reaching into the installer for its COM plumbing
/// would make the installer the thing that breaks when wrapping changes.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ShellLink
{
    /// <summary>
    /// How much room to give the shell for each string it copies out.
    /// <para>
    /// Arguments are the long one: the shell's own limit is <c>INFOTIPSIZE</c>, 1024
    /// characters, and a command line built by a tool's installer gets close. Four
    /// times that costs 8 KB of scratch on one call and removes the question.
    /// </para>
    /// </summary>
    private const int BufferChars = 4096;

    /// <summary>The shortcut asks to run elevated. <c>SLDF_RUNAS_USER</c>.</summary>
    private const int RunAsUserFlag = 0x00002000;

    /// <summary><c>SLGP_RAWPATH</c>: the path as stored, environment variables and all.</summary>
    private const int RawPath = 0x4;

    public static void Write(string linkPath, ShellLinkInfo link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);

        object instance = Activate(ClassIdShellLink, InterfaceIdShellLink);

        try
        {
            var shortcut = (IShellLinkW)instance;

            shortcut.SetPath(link.Target);
            shortcut.SetArguments(link.Arguments);
            shortcut.SetDescription(link.Description);
            shortcut.SetWorkingDirectory(link.WorkingDirectory);

            if (link.IconPath.Length > 0)
            {
                shortcut.SetIconLocation(link.IconPath, link.IconIndex);
            }

            // The cast is a QueryInterface: the source generator makes the wrapper
            // IDynamicInterfaceCastable, so asking for another interface declared here
            // goes out to the object rather than being decided by the type system.
            //
            // fRemember: false writes a copy and leaves the object's own notion of "my
            // file" unset. Passing true makes the shell link adopt the path it just
            // wrote, so the file stays associated with a live COM object and cannot be
            // deleted until that object goes away.
            ((IPersistFile)instance).Save(linkPath, fRemember: false);
        }
        finally
        {
            // Released here rather than left to the finaliser. This is hygiene, not the
            // cure for the delete race - the shell holds the file from outside this
            // process, and removing this line alone does not reintroduce the failure.
            // It is still worth doing: a COM object whose lifetime is decided by the GC
            // is a latent version of exactly that bug.
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Reads a shortcut without resolving it.
    /// <para>
    /// Deliberately no <c>Resolve</c> call. Resolving is what Explorer does when a
    /// target has moved: it searches, and on a bad day it puts a progress dialog on
    /// screen or waits on a dead network path. Reading a file the user just picked
    /// must not be able to hang.
    /// </para>
    /// </summary>
    public static ShellLinkInfo Read(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);

        object instance = Activate(ClassIdShellLink, InterfaceIdShellLink);

        try
        {
            const int StgmRead = 0x0;

            ((IPersistFile)instance).Load(linkPath, StgmRead);

            var shortcut = (IShellLinkW)instance;
            IntPtr buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));

            try
            {
                // The raw path rather than the resolved one, then expanded here. It is
                // the same string in the common case, and in the case that matters -
                // "%LOCALAPPDATA%\Programs\..." - it is the only one the shell will
                // hand back at all.
                string target = Expand(
                    Text(buffer, () => shortcut.GetPath(buffer, BufferChars, IntPtr.Zero, RawPath)));

                string arguments = Text(buffer, () => shortcut.GetArguments(buffer, BufferChars));
                string workingDirectory = Expand(Text(buffer, () => shortcut.GetWorkingDirectory(buffer, BufferChars)));
                string description = Text(buffer, () => shortcut.GetDescription(buffer, BufferChars));

                int iconIndex = 0;
                string iconPath = Text(buffer, () => shortcut.GetIconLocation(buffer, BufferChars, out iconIndex));

                return new ShellLinkInfo(
                    target,
                    arguments,
                    workingDirectory,
                    Expand(iconPath),
                    iconIndex,
                    description,
                    Elevates(instance));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Expands environment variables, and leaves anything it cannot expand alone.
    /// <para>
    /// An unexpanded target is not an error worth throwing over — it is a shortcut
    /// pointing at something that is not on this machine, which the caller reports far
    /// better than an exception from a string function would.
    /// </para>
    /// </summary>
    private static string Expand(string value) =>
        value.Length == 0 ? value : Environment.ExpandEnvironmentVariables(value);

    /// <summary>
    /// Runs one getter and reads what it wrote.
    /// <para>
    /// The buffer is cleared first because several of these return <c>S_FALSE</c> and
    /// write nothing — a shortcut with no arguments, or a Store shortcut with no path
    /// at all. Without clearing, the caller would read back whatever the previous
    /// getter left there, which is how a shortcut comes to be wrapped around the wrong
    /// program.
    /// </para>
    /// </summary>
    private static unsafe string Text(IntPtr buffer, Action get)
    {
        new Span<char>((void*)buffer, BufferChars).Clear();

        get();

        return Marshal.PtrToStringUni(buffer) ?? string.Empty;
    }

    /// <summary>
    /// Whether the shortcut asks to run elevated.
    /// <para>
    /// Through a second interface on the same object, because the flag is not part of
    /// <c>IShellLinkW</c> at all. Treated as "no" when the object refuses the
    /// QueryInterface: a shortcut that cannot be asked is not evidence of elevation,
    /// and refusing to wrap on the strength of a failed call would block the ordinary
    /// case to guard the rare one.
    /// </para>
    /// </summary>
    private static bool Elevates(object instance)
    {
        try
        {
            ((IShellLinkDataList)instance).GetFlags(out int flags);

            return (flags & RunAsUserFlag) != 0;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a COM object and wraps it, without the built-in COM support that
    /// <c>PublishTrimmed</c> switches off.
    /// <para>
    /// Casting a <c>new</c>-ed <c>[ComImport]</c> coclass is the usual way to write this,
    /// and was how this started, but built-in COM is a documented hole in trimming: the
    /// linker cannot see which interfaces survive, so it fails the build, and forcing the
    /// switch back on only moves the failure to a <see cref="NotSupportedException"/> on
    /// the user's machine. That is issue #72. The interfaces below are source-generated
    /// instead, which the linker can follow, leaving only activation to do by hand.
    /// </para>
    /// </summary>
    internal static object Activate(Guid classId, Guid interfaceId)
    {
        const uint ClsCtxInprocServer = 0x1;

        int hr = CoCreateInstance(in classId, IntPtr.Zero, ClsCtxInprocServer, in interfaceId, out IntPtr unknown);

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return Adopt(unknown);
    }

    /// <summary>
    /// Wraps a raw COM pointer and gives back the reference that came with it.
    /// <para>
    /// Shared with the file picker, which receives pointers from out-parameters rather
    /// than from activation. Getting the release wrong in either direction is a leak or
    /// a crash, so there is one copy of it.
    /// </para>
    /// </summary>
    internal static object Adopt(IntPtr unknown)
    {
        try
        {
            // UniqueInstance rather than a cached wrapper, so that disposing actually
            // releases instead of handing the same wrapper to the next caller.
            return Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            // The wrapper took a reference of its own; this is the one the call handed
            // us, and it is ours to give back.
            _ = Marshal.Release(unknown);
        }
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid ClassIdShellLink = new("00021401-0000-0000-C000-000000000046");

    private static readonly Guid InterfaceIdShellLink = new("000214F9-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        // The order and signature of every member must match the vtable exactly - an
        // interface is a layout, not a list of the methods you happen to want. The
        // members that are never called take a raw pointer where the real signature has
        // a string buffer: a slot that exists only to be counted does not need
        // marshalling generated for it. The getters that *are* called take one too,
        // because one buffer is allocated and reused across all of them.
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, int fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(IntPtr pszName, int cch);

        void SetDescription(string pszName);

        void GetWorkingDirectory(IntPtr pszDir, int cch);

        void SetWorkingDirectory(string pszDir);

        void GetArguments(IntPtr pszArgs, int cch);

        void SetArguments(string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);

        void SetIconLocation(string pszIconPath, int iIcon);

        void SetRelativePath(string pszPathRel, int dwReserved);

        void Resolve(IntPtr hwnd, int fFlags);

        void SetPath(string pszFile);
    }

    /// <summary>
    /// The half of a shortcut <c>IShellLinkW</c> does not describe. Only
    /// <c>GetFlags</c> is used, for the one flag that decides whether wrapping is
    /// possible at all.
    /// </summary>
    [GeneratedComInterface]
    [Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1")]
    internal partial interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr pDataBlock);

        void CopyDataBlock(int dwSig, out IntPtr ppDataBlock);

        void RemoveDataBlock(int dwSig);

        void GetFlags(out int pdwFlags);

        void SetFlags(int dwFlags);
    }

    /// <summary>
    /// <c>IPersistFile</c>, which derives from <c>IPersist</c> in COM. Declared flat, with
    /// <c>GetClassID</c> first, because that inherited member owns a vtable slot and the
    /// slots have to line up.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load(string pszFileName, int dwMode);

        void Save(string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted(string pszFileName);

        void GetCurFile(out string ppszFileName);
    }
}
