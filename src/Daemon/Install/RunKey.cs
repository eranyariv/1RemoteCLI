using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// The fallback autostart: a value under <c>HKCU\...\Run</c>.
/// <para>
/// Worse than the task in every way that matters — Windows kills a Run entry that
/// takes too long to start, there is no "don't stop on battery", and the entry is
/// one click away from being disabled forever in Task Manager's Startup tab, where
/// a user who does not recognise the name will disable it. It exists only because
/// task registration can be refused outright by policy on a managed machine, and an
/// agent that starts on a worse trigger is still infinitely better than one that
/// never starts at all.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class RunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Shown in Task Manager's Startup tab, so it says what it is.</summary>
    public const string ValueName = "1RemoteCLI Agent";

    public static StepResult Register(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(Path, writable: true);

            // Quoted, because Run values are parsed as command lines and the default
            // install path contains a space.
            key.SetValue(ValueName, $"\"{exePath}\" agent", RegistryValueKind.String);

            return StepResult.Success("Registered a startup entry as a fallback.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return StepResult.Failure($"Could not write the startup entry: {ex.Message}");
        }
    }

    public static StepResult Remove()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Path, writable: true);

            if (key?.GetValue(ValueName) is null)
            {
                return StepResult.Success("No startup entry was registered.");
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);

            return StepResult.Success("Removed the startup entry.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return StepResult.Failure($"Could not remove the startup entry: {ex.Message}");
        }
    }

    public static bool IsRegistered()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Path);

        return key?.GetValue(ValueName) is not null;
    }
}
