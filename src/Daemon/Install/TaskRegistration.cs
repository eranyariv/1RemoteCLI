using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace OneRemoteCli.Daemon.Install;

/// <summary>The outcome of one installation step, in words a user can act on.</summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Message">What happened. Shown verbatim.</param>
public readonly record struct StepResult(bool Ok, string Message)
{
    public static StepResult Success(string message) => new(true, message);

    public static StepResult Failure(string message) => new(false, message);
}

/// <summary>
/// Registering and removing the logon task, through <c>schtasks.exe</c>.
/// <para>
/// Through the tool rather than the Task Scheduler COM API: the COM interop is a
/// large surface to carry for four operations, and <c>schtasks</c> is present on
/// every Windows install, is what a user would reach for to check the result by
/// hand, and reports its failures as text they can search for.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskRegistration
{
    /// <summary>Registers the logon task, replacing any existing one.</summary>
    /// <param name="taskName">
    /// Overridden only by the tests, so registering a real task on the machine running
    /// them cannot remove the developer's own installed agent.
    /// </param>
    public static StepResult Register(string exePath, string userId, string? taskName = null)
    {
        taskName ??= AgentTask.TaskName;

        string xml = AgentTask.BuildXml(exePath, userId);

        // A real file, because schtasks /XML takes a path. Deliberately in the temp
        // directory and deleted straight after: it contains nothing secret, but a
        // stray task definition next to the executable invites someone to edit it and
        // wonder why nothing changes.
        string path = Path.Combine(Path.GetTempPath(), $"1remote-task-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16, and this is not a preference. schtasks /XML rejects a UTF-8 file
            // with "The task XML is malformed", which points at the XML rather than at
            // the encoding and has cost people whole afternoons.
            File.WriteAllText(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            (int code, string output) = Run("/Create", "/TN", taskName, "/XML", path, "/F");

            return code == 0
                ? StepResult.Success($"Registered the logon task '{taskName}'.")
                : StepResult.Failure($"Could not register the logon task: {Summarise(output)}");
        }
        catch (IOException ex)
        {
            return StepResult.Failure($"Could not write the task definition: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing an install over.
            }
        }
    }

    /// <summary>Removes the logon task. Reports success when there was nothing to remove.</summary>
    public static StepResult Remove(string? taskName = null)
    {
        taskName ??= AgentTask.TaskName;

        if (!IsRegistered(taskName))
        {
            return StepResult.Success("No logon task was registered.");
        }

        (int code, string output) = Run("/Delete", "/TN", taskName, "/F");

        return code == 0
            ? StepResult.Success($"Removed the logon task '{taskName}'.")
            : StepResult.Failure($"Could not remove the logon task: {Summarise(output)}");
    }

    public static bool IsRegistered(string? taskName = null) => Run("/Query", "/TN", taskName ?? AgentTask.TaskName).Code == 0;

    /// <summary>Starts the task now, so an install does not require a logout to take effect.</summary>
    public static StepResult RunNow()
    {
        (int code, string output) = Run("/Run", "/TN", AgentTask.TaskName);

        return code == 0
            ? StepResult.Success("Started the agent.")
            : StepResult.Failure($"Could not start the agent: {Summarise(output)}");
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "schtasks.exe did not start.");
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// The one useful line out of schtasks' output.
    /// <para>
    /// It prints a banner, a blank line and then the error, and pasting all of that
    /// into a terminal buries the part that matters.
    /// </para>
    /// </summary>
    private static string Summarise(string output)
    {
        string[] lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();

        return lines.LastOrDefault() ?? "schtasks reported no reason.";
    }
}
