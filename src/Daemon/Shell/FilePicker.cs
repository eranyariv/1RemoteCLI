using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Shell;

/// <summary>
/// The shell's own Open dialog, for picking the shortcut to wrap.
/// <para>
/// <c>IFileOpenDialog</c> rather than Windows Forms' <c>OpenFileDialog</c>. The tray
/// was rewritten onto raw <c>Shell_NotifyIcon</c> in issue #46 precisely to get the
/// Windows Desktop runtime out of the download — 22 MB — and a single Windows Forms
/// type anywhere in the executable would put all of it back. This is the dialog
/// Explorer itself shows, and it is what a user recognises.
/// </para>
/// <para>
/// Must be called on a single-threaded-apartment thread. The tray's message loop
/// thread is one, which is also the thread the settings window lives on, so the
/// dialog is modal to the window that opened it without any extra arrangement.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class FilePicker
{
    /// <summary>
    /// Asks for a shortcut, and gives back null if the user changed their mind.
    /// <para>
    /// Cancelling is not an error and must not read like one: the shell reports it as
    /// a failed <c>HRESULT</c>, and translating that into an exception would turn the
    /// most common outcome into the noisiest.
    /// </para>
    /// </summary>
    /// <param name="owner">The window to be modal to, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="title">The dialog's caption.</param>
    /// <param name="folder">Where to start. Ignored if it does not exist.</param>
    public static string? PickShortcut(IntPtr owner, string title, string? folder = null)
    {
        // Cancelled, as the shell reports it: HRESULT_FROM_WIN32(ERROR_CANCELLED).
        const int Cancelled = unchecked((int)0x800704C7);

        const int ForceFilesystem = 0x40;
        const int PathMustExist = 0x800;
        const int FileMustExist = 0x1000;

        // The dialog is not the user's file manager, and letting it change this
        // process's current directory would move where every later relative path
        // resolves - including a session's.
        const int NoChangeDirectory = 0x8;

        object instance = ShellLink.Activate(ClassIdFileOpenDialog, InterfaceIdFileOpenDialog);

        try
        {
            var dialog = (IFileOpenDialog)instance;

            dialog.SetTitle(title);
            dialog.SetOptions(ForceFilesystem | PathMustExist | FileMustExist | NoChangeDirectory);

            IntPtr filters = Filters(out IntPtr[] strings);

            try
            {
                dialog.SetFileTypes(2, filters);
                dialog.SetFileTypeIndex(1);

                StartIn(dialog, folder);

                int hr = dialog.Show(owner);

                if (hr == Cancelled)
                {
                    return null;
                }

                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                dialog.GetResult(out IntPtr chosen);

                return PathOf(chosen);
            }
            finally
            {
                Free(filters, strings);
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Creates the dialog and throws it away, for <c>1remote self-check</c>.
    /// <para>
    /// Activation is what trimming breaks, and the only way to find out is to do it.
    /// The published build creates this object by hand through <c>CoCreateInstance</c>
    /// because built-in COM is off (issue #72), so a wrong class id, an unregistered
    /// class or a trimmed interface all surface here rather than the first time a user
    /// clicks the button.
    /// </para>
    /// </summary>
    public static bool CanActivate()
    {
        object? instance = null;

        try
        {
            instance = ShellLink.Activate(ClassIdFileOpenDialog, InterfaceIdFileOpenDialog);

            // Cast as well as create: the object comes back as a wrapper, and whether
            // the interface is really there is only settled when it is asked for.
            return instance is IFileOpenDialog;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Points the dialog at a folder, if there is one to point it at.
    /// <para>
    /// Failure here is deliberately swallowed. Opening in the wrong folder is a small
    /// inconvenience; refusing to open at all because the Desktop could not be resolved
    /// is not a trade worth making.
    /// </para>
    /// </summary>
    private static void StartIn(IFileOpenDialog dialog, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Guid shellItem = InterfaceIdShellItem;

            if (SHCreateItemFromParsingName(folder, IntPtr.Zero, in shellItem, out IntPtr item) < 0)
            {
                return;
            }

            try
            {
                dialog.SetFolder(item);
            }
            finally
            {
                _ = Marshal.Release(item);
            }
        }
        catch (COMException)
        {
        }
    }

    /// <summary>Reads the chosen item's path and releases it.</summary>
    private static string? PathOf(IntPtr chosen)
    {
        if (chosen == IntPtr.Zero)
        {
            return null;
        }

        // The full filesystem path. FORCEFILESYSTEM above is what guarantees there is
        // one - without it a user can pick a library or a search result, which has a
        // display name and no file behind it.
        const uint FileSystemPath = 0x80058000;

        object item = ShellLink.Adopt(chosen);

        try
        {
            ((IShellItem)item).GetDisplayName(FileSystemPath, out IntPtr name);

            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                // The shell allocated it with the COM task allocator, so it comes back
                // the same way.
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            (item as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Builds the <c>COMDLG_FILTERSPEC</c> array by hand: two string pointers per
    /// entry, laid out end to end. It is a fixed, two-element table, and declaring a
    /// marshalled struct for it would be more machinery than the thing it describes.
    /// </summary>
    private static IntPtr Filters(out IntPtr[] strings)
    {
        strings =
        [
            Marshal.StringToCoTaskMemUni("Shortcuts"),
            Marshal.StringToCoTaskMemUni("*.lnk"),

            // Second, and present at all because a tool that ships a shortcut somewhere
            // other than the Desktop is easier to find by looking than by filtering.
            Marshal.StringToCoTaskMemUni("All files"),
            Marshal.StringToCoTaskMemUni("*.*"),
        ];

        IntPtr block = Marshal.AllocCoTaskMem(IntPtr.Size * strings.Length);
        Marshal.Copy(strings, 0, block, strings.Length);

        return block;
    }

    private static void Free(IntPtr block, IntPtr[] strings)
    {
        Marshal.FreeCoTaskMem(block);

        foreach (IntPtr text in strings)
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static readonly Guid ClassIdFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    private static readonly Guid InterfaceIdFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    private static readonly Guid InterfaceIdShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        out IntPtr ppv);

    /// <summary>
    /// <c>IFileOpenDialog</c>, flattened through <c>IFileDialog</c> and
    /// <c>IModalWindow</c>. Every inherited member owns a vtable slot, so all of them
    /// are declared and in order even though six are used; the ones that are not take
    /// raw pointers, since a slot that exists only to be counted needs no marshalling.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    internal partial interface IFileOpenDialog
    {
        /// <summary>
        /// The one call whose failure is routine: the user pressing Cancel arrives
        /// here as an <c>HRESULT</c>, so this slot keeps it rather than throwing.
        /// </summary>
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(int cFileTypes, IntPtr rgFilterSpec);

        void SetFileTypeIndex(int iFileType);

        void GetFileTypeIndex(out int piFileType);

        void Advise(IntPtr pfde, out int pdwCookie);

        void Unadvise(int dwCookie);

        void SetOptions(int fos);

        void GetOptions(out int pfos);

        void SetDefaultFolder(IntPtr psi);

        void SetFolder(IntPtr psi);

        void GetFolder(out IntPtr ppsi);

        void GetCurrentSelection(out IntPtr ppsi);

        void SetFileName(string pszName);

        void GetFileName(out IntPtr pszName);

        void SetTitle(string pszTitle);

        void SetOkButtonLabel(string pszText);

        void SetFileNameLabel(string pszLabel);

        void GetResult(out IntPtr ppsi);

        void AddPlace(IntPtr psi, int fdap);

        void SetDefaultExtension(string pszDefaultExtension);

        void Close(int hr);

        void SetClientGuid(in Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr pFilter);

        void GetResults(out IntPtr ppenum);

        void GetSelectedItems(out IntPtr ppsai);
    }

    /// <summary>One thing in the shell namespace. Only its path is ever wanted.</summary>
    [GeneratedComInterface]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal partial interface IShellItem
    {
        void BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);

        void GetParent(out IntPtr ppsi);

        void GetDisplayName(uint sigdnName, out IntPtr ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IntPtr psi, uint hint, out int piOrder);
    }
}
