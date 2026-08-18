using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Shell;

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
/// The shortcuts themselves are written by <see cref="ShellLink"/>, which is shared
/// with shortcut wrapping. This file is about which entries exist and where; that one
/// is about the file format.
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

            string home = Path.GetDirectoryName(exePath) ?? string.Empty;

            ShellLink.Write(
                Path.Combine(folder, "Sign in to 1RemoteCLI.lnk"),
                new ShellLinkInfo(
                    exePath,
                    "login",
                    home,
                    Description: "Sign in so this machine's sessions reach your phone."));

            ShellLink.Write(
                Path.Combine(folder, "Start 1RemoteCLI agent.lnk"),
                new ShellLinkInfo(
                    exePath,
                    "agent",
                    home,
                    Description: "Start the agent that keeps this machine reachable."));

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
}
