using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OneRemoteCli.Daemon.Update;

/// <summary>How an update attempt ended.</summary>
/// <param name="Ok">Whether the machine is now on the release that was asked for.</param>
/// <param name="Message">What to tell the user, in one sentence.</param>
/// <param name="Replaced">
/// Whether the program file actually changed. False for a machine that was already on
/// that build, which is a success that needs no restart.
/// </param>
public readonly record struct UpdateResult(bool Ok, string Message, bool Replaced)
{
    public static UpdateResult Failure(string message) => new(false, message, false);
}

/// <summary>
/// The three things applying an update does that cannot be done in a test: fetch from
/// github.com, run a program, and move a file that Windows is executing.
/// <para>
/// Delegates rather than an interface, like <see cref="Tray.SettingsActions"/>, so the
/// order and the refusals — which are the whole of the risk here — are testable without
/// a network, and without a test being one bug away from replacing the executable it
/// is running under.
/// </para>
/// </summary>
/// <param name="Download">One asset of the release, by name.</param>
/// <param name="Prove">
/// Runs a downloaded executable and returns the version it printed, or null if it would
/// not run at all. See <see cref="AgentUpdate.ProveByRunning"/> for why this exists.
/// </param>
/// <param name="Replace">
/// Puts the staged file where the installed one is. Returns why not, or null when it
/// worked; a failure must leave the installed file as it was.
/// </param>
public sealed record UpdateSteps(
    Func<string, CancellationToken, Task<byte[]>> Download,
    Func<string, string?> Prove,
    Func<string, string, string?> Replace);

/// <summary>
/// Replacing this machine's <c>1remote.exe</c> with a newer release.
/// <para>
/// The sequence is the one <c>scripts/install.ps1</c> performs, for the same reasons
/// and in the same order, with one step added. The installer is run by a person who is
/// watching it and can re-run it; this runs by itself on a machine whose owner is
/// somewhere else, so a bad outcome here is not "try again" but "the tray icon is gone
/// and the phone cannot see the machine". Every refusal below is a case where doing
/// nothing is the better answer:
/// </para>
/// <list type="bullet">
/// <item>No published hash for the asset, or a hash that does not match: nothing is
/// installed. A download URL that cannot resolve answers with an HTML page and a 200,
/// which otherwise lands on disk looking like a program (issue #102's neighbour).</item>
/// <item>Bytes identical to what is installed: the file is not touched at all. Windows
/// judges an executable as it is written and its verdict is not stable between two
/// writes of the same bytes — the rule behind issue #92 refused three of four launches
/// of one unchanged file — so a pointless copy is a real risk of breaking a working
/// install (issue #108).</item>
/// <item>The new build will not run, or does not report the version it was supposed
/// to be: the old one stays. This is the step the installer does not have, and it is
/// the one that makes an automatic update defensible — issues #92, #93 and #101 are all
/// "the executable arrived and then would not start", which is precisely the failure a
/// machine nobody is sitting at cannot recover from.</item>
/// </list>
/// <para>
/// What it does not do is restart the agent. Wrappers do not reconnect — a session
/// whose agent goes away keeps running at the desk and is never shareable again — so
/// deciding when it is safe to restart belongs to whoever can see how many sessions are
/// live. See <see cref="UpdateService"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentUpdate
{
    /// <summary>
    /// Suffix given to the executable being replaced. Windows will rename a running
    /// image but not delete one, which is what makes replacing a live agent possible at
    /// all; the old file is swept up by a later update rather than now.
    /// </summary>
    internal const string RetiredSuffix = ".old";

    /// <summary>
    /// Fetches, checks and installs one release.
    /// <para>
    /// The checksums come first and the program second, so a release that cannot be
    /// verified costs a few hundred bytes instead of thirty megabytes on somebody's
    /// tethered connection.
    /// </para>
    /// </summary>
    public static async Task<UpdateResult> ApplyAsync(
        string tag,
        string asset,
        string installedPath,
        string stagingDirectory,
        UpdateSteps steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(steps);

        string? expected;

        try
        {
            byte[] sums = await steps.Download(ReleaseSource.ChecksumsAsset, cancellationToken).ConfigureAwait(false);
            expected = Sha256Sums.Find(Encoding.UTF8.GetString(sums), asset);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateResult.Failure($"Could not read the checksums for {tag}: {ex.Message}");
        }

        if (expected is null)
        {
            return UpdateResult.Failure(
                $"{tag} does not publish a checksum for {asset}, so there is nothing to check a download against.");
        }

        // Before the download, not after: a machine that is already on this build has
        // nothing to fetch, and this is the common answer for anyone who installed
        // moments before the check ran.
        if (File.Exists(installedPath) && string.Equals(HashOf(installedPath), expected, StringComparison.Ordinal))
        {
            return new UpdateResult(true, $"Already running the {tag} build.", Replaced: false);
        }

        byte[] program;

        try
        {
            program = await steps.Download(asset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateResult.Failure($"Could not download {asset} from {tag}: {ex.Message}");
        }

        string actual = Convert.ToHexString(SHA256.HashData(program)).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return UpdateResult.Failure(
                $"The download of {asset} does not match the checksum {tag} publishes, so it has NOT been installed.");
        }

        string staged = Path.Combine(stagingDirectory, asset);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await File.WriteAllBytesAsync(staged, program, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateResult.Failure($"Could not write the download to {stagingDirectory}: {ex.Message}");
        }

        string? reported = steps.Prove(staged);

        if (reported is null)
        {
            return UpdateResult.Failure(
                $"The {tag} download is genuine but would not run on this machine, so it has NOT been installed. The version you have is untouched.");
        }

        if (!ReleaseVersion.TryParse(reported, out ReleaseVersion ran)
            || !ReleaseVersion.TryParse(tag, out ReleaseVersion wanted)
            || ran.CompareTo(wanted) != 0)
        {
            return UpdateResult.Failure(
                $"The {tag} download runs but reports itself as '{reported}', so it has NOT been installed.");
        }

        string? failure = steps.Replace(staged, installedPath);

        return failure is null
            ? new UpdateResult(true, $"Updated to {tag}.", Replaced: true)
            : UpdateResult.Failure($"Could not replace {installedPath}: {failure}");
    }

    /// <summary>The real steps, against github.com and this machine.</summary>
    public static UpdateSteps StepsFor(HttpClient http, string tag)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        return new UpdateSteps(
            async (asset, cancellationToken) =>
                await http.GetByteArrayAsync(ReleaseSource.Download(tag, asset), cancellationToken).ConfigureAwait(false),
            ProveByRunning,
            ReplaceExecutable);
    }

    /// <summary>
    /// Runs a downloaded executable with <c>--version</c> and returns what it printed.
    /// <para>
    /// The cheapest thing this build can be asked to do that still exercises the whole
    /// of what has ever failed: the loader, the runtime, and whatever the machine's
    /// policy thinks of a program that appeared a second ago. It prints one line and
    /// exits, so it can be waited on with a short timeout and cannot leave anything
    /// behind.
    /// </para>
    /// <para>
    /// Null for every kind of not-working, including a non-zero exit and a program that
    /// never returns — the caller's job is to leave the old build alone, and it does not
    /// matter which way the new one was broken.
    /// </para>
    /// </summary>
    internal static string? ProveByRunning(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    // It exited between the wait timing out and the kill. Either way it
                    // did not answer in time, which is the only thing being decided.
                }

                return null;
            }

            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } version ? version : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves the staged build into place, over an executable that may be running.
    /// <para>
    /// Windows refuses to delete or overwrite a running image but will happily rename
    /// one, and a process keeps running from the file it was started from whatever that
    /// file is now called. So the installed executable is renamed out of the way rather
    /// than replaced, which is what lets an agent update itself without first stopping —
    /// and stopping first is not available here, because a stopped agent whose
    /// replacement then fails to arrive is a machine that has gone off the air.
    /// </para>
    /// <para>
    /// If the copy fails, the renamed file goes straight back. That is the whole of the
    /// rollback, and it is why the rename is done rather than a delete.
    /// </para>
    /// </summary>
    internal static string? ReplaceExecutable(string staged, string installed)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        string? retired = null;

        try
        {
            if (File.Exists(installed))
            {
                retired = RetiredPathFor(installed);
                File.Move(installed, retired);
            }

            File.Copy(staged, installed, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (retired is not null && File.Exists(retired) && !File.Exists(installed))
            {
                try
                {
                    File.Move(retired, installed);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    return $"{ex.Message} (and the previous version could not be put back: {rollback.Message})";
                }
            }

            return ex.Message;
        }

        // Best effort, and expected to fail while the old agent is still running from
        // it. A later update sweeps it up once nothing holds it.
        if (retired is not null)
        {
            Delete(retired);
        }

        return null;
    }

    /// <summary>
    /// A free name to rename the installed executable to.
    /// <para>
    /// Usually <c>1remote.exe.old</c>. But that name can still be held: an agent that
    /// installed an update while sessions were open goes on running from the retired
    /// copy, and a second update arriving before those sessions end finds a file it can
    /// neither delete nor rename over. Falling back to a numbered name means the second
    /// update succeeds instead of failing on the leavings of the first — the machine
    /// this matters most on being the one that is never idle.
    /// </para>
    /// </summary>
    private static string RetiredPathFor(string installed)
    {
        string first = installed + RetiredSuffix;

        // Nothing has been started from it since the update that made it, so deleting
        // it is safe when it is not held.
        Delete(first);

        if (!File.Exists(first))
        {
            return first;
        }

        for (int i = 2; i < 100; i++)
        {
            string candidate = $"{first}.{i.ToString(CultureInfo.InvariantCulture)}";

            Delete(candidate);

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // A hundred held-open copies is not a case worth a better answer than the plain
        // name and the failure it produces.
        return first;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open, almost certainly by the process this file was started as.
        }
    }

    private static string? HashOf(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
