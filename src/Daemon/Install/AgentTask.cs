using System.Xml.Linq;

namespace OneRemoteCli.Daemon.Install;

/// <summary>
/// The logon task that makes the agent simply be there.
/// <para>
/// A Scheduled Task rather than a Windows Service, and this is the load-bearing
/// decision of the whole product: the agent has to run as the interactive user, in
/// their session, with their environment and their token cache. A service runs in
/// session 0 and can neither see nor be seen by the console the user is typing at.
/// </para>
/// <para>
/// Built as XML rather than through <c>schtasks</c>'s flag syntax because several of
/// the settings below cannot be expressed as flags at all, and every one of them
/// fails the same way: the agent works perfectly until some unrelated condition
/// changes hours later, and then is simply gone, with nothing in any log to say why.
/// </para>
/// </summary>
public static class AgentTask
{
    /// <summary>Visible in Task Scheduler, so it is a sentence rather than an identifier.</summary>
    public const string TaskName = "1RemoteCLI Agent";

    /// <summary>The task definition.</summary>
    /// <param name="exePath">Full path to <c>1remote.exe</c>.</param>
    /// <param name="userId">The account to run as, as <c>DOMAIN\user</c>.</param>
    public static string BuildXml(string exePath, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(
                ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(
                    ns + "RegistrationInfo",
                    new XElement(
                        ns + "Description",
                        "Keeps 1RemoteCLI's agent running so terminal sessions on this machine stay reachable from your phone."),
                    new XElement(ns + "URI", $"\\{TaskName}")),
                new XElement(
                    ns + "Triggers",
                    new XElement(
                        ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        // Scoped to this user. Without it the task fires for every account
                        // that logs on, and each would try to claim the same pipe name.
                        new XElement(ns + "UserId", userId))),
                new XElement(
                    ns + "Principals",
                    new XElement(
                        ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userId),
                        // The whole point. InteractiveToken means "only when this user is
                        // logged on, in their session" - which is where the terminals are.
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    ns + "Settings",
                    // Each of the first three is a default that silently kills a
                    // long-running agent, hours after install, with no error anywhere.
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(
                        ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "false"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "true"),
                    // A second agent for the same user cannot work - they would fight over
                    // one pipe name - so a re-trigger must leave the running one alone.
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "Priority", "7")),
                new XElement(
                    ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        ns + "Exec",
                        new XElement(ns + "Command", exePath),
                        new XElement(ns + "Arguments", "agent")))));

        using var writer = new StringWriter();
        document.Save(writer);

        return writer.ToString();
    }
}
