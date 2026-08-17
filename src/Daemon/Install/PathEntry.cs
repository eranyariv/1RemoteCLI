using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// Putting <c>1remote</c> on the user's <c>PATH</c>.
/// <para>
/// Without this, <c>1remote install</c> leaves a machine where the agent runs but the
/// command that manages it cannot be typed. Every instruction in the docs — <c>1remote
/// login</c>, <c>1remote status</c>, <c>1remote wrap claude</c> — is then wrong for
/// the person who most needs it, the one who has just installed it.
/// </para>
/// <para>
/// User scope, never machine scope: the whole product installs per user, under
/// <c>%LOCALAPPDATA%</c>, with a per-user logon task and a per-user token cache.
/// Writing the machine PATH would need elevation for no benefit and would put one
/// user's private directory on every account's PATH.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PathEntry
{
    private const string EnvironmentKey = "Environment";
    private const string ValueName = "Path";

    /// <summary>
    /// Adds the directory holding <paramref name="exePath"/>, if it is not already there.
    /// </summary>
    public static StepResult Register(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        string directory = Directory(exePath);

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true);

            (string current, RegistryValueKind kind) = Read(key);

            if (Adding(current, directory) is not { } updated)
            {
                return StepResult.Success("Already on your PATH.");
            }

            key.SetValue(ValueName, updated, kind);
            Announce();

            return StepResult.Success("Added to your PATH. Open a new terminal to use '1remote'.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return StepResult.Failure($"Could not add to your PATH: {ex.Message}");
        }
    }

    public static StepResult Remove(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        string directory = Directory(exePath);

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: true);

            if (key is null)
            {
                return StepResult.Success("Not on your PATH.");
            }

            (string current, RegistryValueKind kind) = Read(key);

            if (Removing(current, directory) is not { } updated)
            {
                return StepResult.Success("Not on your PATH.");
            }

            key.SetValue(ValueName, updated, kind);
            Announce();

            return StepResult.Success("Removed from your PATH.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return StepResult.Failure($"Could not remove from your PATH: {ex.Message}");
        }
    }

    /// <summary>
    /// The PATH that should replace <paramref name="current"/>, or <c>null</c> if the
    /// directory is already on it.
    /// <para>
    /// Separated from the registry so the string handling — which is where the damage
    /// would be done — can be tested without a test ever writing to the PATH of the
    /// machine running it.
    /// </para>
    /// </summary>
    internal static string? Adding(string current, string directory)
    {
        if (Contains(current, directory))
        {
            return null;
        }

        return current.Trim().Length == 0 ? directory : $"{current.TrimEnd(';')};{directory}";
    }

    /// <summary>The PATH with the directory taken out, or <c>null</c> if it was not on it.</summary>
    internal static string? Removing(string current, string directory)
    {
        if (!Contains(current, directory))
        {
            return null;
        }

        return string.Join(';', Entries(current).Where(entry => !Same(entry, directory)));
    }

    /// <summary>
    /// Reads the raw value, unexpanded, and reports how it was stored.
    /// <para>
    /// <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> is the whole
    /// point. A user PATH is usually <c>REG_EXPAND_SZ</c> and full of entries like
    /// <c>%USERPROFILE%\.dotnet\tools</c>. Read it the ordinary way and those come back
    /// already expanded; write that back and the references are gone for good,
    /// silently, on a machine where the folders they point at may later move. Appending
    /// one entry must not rewrite the other twenty.
    /// </para>
    /// </summary>
    private static (string Value, RegistryValueKind Kind) Read(RegistryKey key)
    {
        object? raw = key.GetValue(ValueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);

        // GetValueKind throws when the value is absent, which is a perfectly ordinary
        // state for a fresh profile with no user PATH of its own.
        RegistryValueKind kind = RegistryValueKind.ExpandString;

        if (key.GetValue(ValueName) is not null && key.GetValueKind(ValueName) is RegistryValueKind.String)
        {
            kind = RegistryValueKind.String;
        }

        return (raw as string ?? string.Empty, kind);
    }

    private static string Directory(string exePath) =>
        Path.GetDirectoryName(Path.GetFullPath(exePath))
        ?? throw new ArgumentException("The executable path has no directory.", nameof(exePath));

    private static IEnumerable<string> Entries(string path) =>
        path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Contains(string path, string directory) =>
        Entries(path).Any(entry => Same(entry, directory));

    /// <summary>
    /// Compares two PATH entries the way Windows does — case-insensitively, and
    /// ignoring a trailing separator, which is written inconsistently everywhere.
    /// Deliberately textual: resolving each entry would touch the filesystem for every
    /// directory on the PATH, including dead network drives that block for seconds.
    /// </summary>
    private static bool Same(string entry, string directory) =>
        string.Equals(
            entry.TrimEnd('\\', '/'),
            directory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tells everything already running that the environment changed.
    /// <para>
    /// Without this the new PATH reaches only processes started after the next logon,
    /// because Explorer hands its own copy to everything it launches. With it, a
    /// terminal opened from the taskbar a moment later has the entry.
    /// </para>
    /// <para>
    /// Sent with a timeout and its result ignored: a hung window must not be able to
    /// fail an install that has already succeeded, and the registry — which is the
    /// durable part — is written either way.
    /// </para>
    /// </summary>
    private static void Announce()
    {
        const int HWND_BROADCAST = 0xffff;
        const int WM_SETTINGCHANGE = 0x001a;
        const int SMTO_ABORTIFHUNG = 0x0002;

        try
        {
            _ = SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_SETTINGCHANGE,
                UIntPtr.Zero,
                EnvironmentKey,
                SMTO_ABORTIFHUNG,
                1000,
                out _);
        }
        catch (EntryPointNotFoundException)
        {
            // Nothing to do about it, and nothing worth telling the user: the PATH is
            // written, it simply will not be seen until they open a new terminal.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        UIntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out UIntPtr result);
}
