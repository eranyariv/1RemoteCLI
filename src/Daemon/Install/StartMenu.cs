using System.Runtime.InteropServices;
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
public static class StartMenu
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
        var link = (IShellLinkW)new ShellLink();

        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);

            // fRemember: false writes a copy and leaves the object's own notion of "my
            // file" unset. Passing true makes the shell link adopt the path it just
            // wrote, so the file stays associated with a live COM object and cannot be
            // deleted until that object goes away.
            ((IPersistFile)link).Save(linkPath, fRemember: false);
        }
        finally
        {
            // Released here rather than left to the finaliser. This is hygiene, not the
            // cure for the delete race - the shell holds the file from outside this
            // process, and removing this line alone does not reintroduce the failure.
            // It is still worth doing: a COM object whose lifetime is decided by the GC
            // is a latent version of exactly that bug.
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// The <c>ShellLink</c> coclass. Not sealed: the cast to <see cref="IShellLinkW"/>
    /// is only legal on a type the compiler cannot prove does not implement it, and the
    /// runtime supplies the actual implementation.
    /// </summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Only the setters are declared, but the order and signature of every member
        // must match the vtable exactly - an interface is a layout, not a list of the
        // methods you happen to want.
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            int fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);

        void Resolve(IntPtr hwnd, int fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
