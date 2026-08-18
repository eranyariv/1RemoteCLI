using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// The Start Menu entries, so <c>1remote</c> is findable by someone who has
/// forgotten it exists.
/// <para>
/// Two shortcuts, both to the same executable: <em>Sign in</em>, which is the only
/// thing a new user has to do by hand, and <em>Start agent</em>, for the case where
/// the logon task has not fired yet. Deliberately not a shortcut to the wrapper —
/// launching <c>1remote</c> with no arguments from Explorer just prints usage into a
/// window that closes instantly.
/// </para>
/// <para>
/// A <c>.lnk</c> is a structured binary format with no managed writer, so this goes
/// through the same COM interface Explorer itself uses. The alternative — hand-rolling
/// the format — produces files that work until Windows tightens something.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class StartMenu
{
    /// <summary>Under the user's own Start Menu, so no elevation is needed.</summary>
    public static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "1RemoteCLI");

    /// <param name="folder">
    /// Overridden only by the tests, so a suite run does not rewrite the developer's
    /// real Start Menu.
    /// </param>
    public static StepResult Install(string exePath, string? folder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        folder ??= FolderPath;

        try
        {
            Directory.CreateDirectory(folder);

            CreateShortcut(
                Path.Combine(folder, "Sign in to 1RemoteCLI.lnk"),
                exePath,
                "login",
                "Sign in so this machine's sessions reach your phone.");

            CreateShortcut(
                Path.Combine(folder, "Start 1RemoteCLI agent.lnk"),
                exePath,
                "agent",
                "Start the agent that keeps this machine reachable.");

            return StepResult.Success("Added Start Menu entries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            return StepResult.Failure($"Could not create Start Menu entries: {ex.Message}");
        }
    }

    public static StepResult Remove(string? folder = null)
    {
        folder ??= FolderPath;

        try
        {
            if (!Directory.Exists(folder))
            {
                return StepResult.Success("No Start Menu entries were present.");
            }

            DeleteWithRetry(folder);

            return StepResult.Success("Removed Start Menu entries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepResult.Failure($"Could not remove Start Menu entries: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the folder, giving the shell a moment to let go first.
    /// <para>
    /// A newly written <c>.lnk</c> is opened by the Start Menu indexer within
    /// milliseconds, and while it is being read the file cannot be deleted. Uninstalling
    /// shortly after installing therefore fails through no fault of the user's. The
    /// holder is outside this process — releasing our own COM objects first is not
    /// enough, which was checked rather than assumed — so waiting the window out is the
    /// only fix available. Bounded, because a genuine permission problem must still
    /// surface rather than hang.
    /// </para>
    /// </summary>
    private static void DeleteWithRetry(string folder)
    {
        const int attempts = 10;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);

                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void CreateShortcut(string linkPath, string target, string arguments, string description)
    {
        object instance = Activate(ClassIdShellLink, InterfaceIdShellLink);

        try
        {
            var link = (IShellLinkW)instance;

            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);

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
    private static object Activate(Guid classId, Guid interfaceId)
    {
        const uint ClsCtxInprocServer = 0x1;

        int hr = CoCreateInstance(in classId, IntPtr.Zero, ClsCtxInprocServer, in interfaceId, out IntPtr unknown);

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            // UniqueInstance rather than a cached wrapper, so that disposing above
            // actually releases instead of handing the same wrapper to the next caller.
            return Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            // The wrapper took a reference of its own; this is the one CoCreateInstance
            // handed us, and it is ours to give back.
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
        // Only the setters are ever called, but the order and signature of every member
        // must match the vtable exactly - an interface is a layout, not a list of the
        // methods you happen to want. The members that are never called take a raw
        // pointer where the real signature has a string buffer: a slot that exists only
        // to be counted does not need marshalling generated for it.
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
