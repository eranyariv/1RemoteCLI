using System.Xml.Linq;
using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The task XML.
/// <para>
/// Asserted by querying the document rather than by matching strings, because the
/// thing that matters is the value Task Scheduler will read, not the text we happened
/// to write. Every assertion here corresponds to a way the agent dies silently hours
/// after a successful install.
/// </para>
/// </summary>
public sealed class AgentTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private const string Exe = @"C:\Program Files\1RemoteCLI\1remote.exe";
    private const string User = @"CONTOSO\ada";

    private static XElement Build(string exe = Exe, string user = User) =>
        XDocument.Parse(AgentTask.BuildXml(exe, user)).Root!;

    private static string? Value(XElement root, string name) =>
        root.Descendants(Ns + name).FirstOrDefault()?.Value;

    [Fact]
    public void TheTaskRunsInTheUsersOwnSessionWhereTheirTerminalsAre()
    {
        // A service, or a task with any other logon type, lands in session 0 and can
        // never see the console the user types at. This one value is why the agent is
        // a scheduled task at all.
        Assert.Equal("InteractiveToken", Value(Build(), "LogonType"));
    }

    [Fact]
    public void TheTaskDoesNotAskForAdministrator()
    {
        // Elevation would put the agent on a different desktop integrity level from
        // the terminals it wraps, and would prompt at install for no benefit.
        Assert.Equal("LeastPrivilege", Value(Build(), "RunLevel"));
    }

    [Theory]
    // Unplugging the laptop must not stop the agent; this default does exactly that.
    [InlineData("StopIfGoingOnBatteries", "false")]
    [InlineData("DisallowStartIfOnBatteries", "false")]
    // Walking away must not stop it either.
    [InlineData("StopOnIdleEnd", "false")]
    [InlineData("RunOnlyIfIdle", "false")]
    // The default is three days, after which the agent vanishes mid-week.
    [InlineData("ExecutionTimeLimit", "PT0S")]
    // A second agent would fight the first for the same pipe name.
    [InlineData("MultipleInstancesPolicy", "IgnoreNew")]
    // No console window flashing up at every logon.
    [InlineData("Hidden", "true")]
    // The agent is what makes the machine reachable; waiting for a network it is
    // supposed to be waiting on is circular.
    [InlineData("RunOnlyIfNetworkAvailable", "false")]
    public void SettingsThatWouldSilentlyKillTheAgentAreOff(string element, string expected) =>
        Assert.Equal(expected, Value(Build(), element));

    [Fact]
    public void TheTriggerIsScopedToOneUser()
    {
        XElement trigger = Build().Descendants(Ns + "LogonTrigger").Single();

        // Without a UserId the task fires for every account that logs on to the
        // machine, and each copy would claim the same pipe.
        Assert.Equal(User, trigger.Element(Ns + "UserId")?.Value);
        Assert.Equal("true", trigger.Element(Ns + "Enabled")?.Value);
    }

    [Fact]
    public void TheActionRunsTheAgentAndNothingElse()
    {
        XElement exec = Build().Descendants(Ns + "Exec").Single();

        Assert.Equal(Exe, exec.Element(Ns + "Command")?.Value);
        Assert.Equal("agent", exec.Element(Ns + "Arguments")?.Value);
    }

    [Fact]
    public void TheSchemaVersionIsOneTheParserAccepts()
    {
        XElement root = Build();

        Assert.Equal("Task", root.Name.LocalName);
        Assert.Equal(Ns, root.Name.Namespace);

        // 1.2 is the oldest version carrying every setting above. Claiming 1.4 would
        // refuse to register on Windows builds that are still perfectly capable.
        Assert.Equal("1.2", root.Attribute("version")?.Value);
    }

    [Fact]
    public void APathWithSpacesSurvivesUnquoted()
    {
        // The XML form is why: in flag form this path would need quoting, and a quoted
        // path inside a quoted /TR argument is where schtasks command lines go wrong.
        Assert.Equal(Exe, Build().Descendants(Ns + "Command").Single().Value);
    }

    [Theory]
    [InlineData("", User)]
    [InlineData("  ", User)]
    [InlineData(Exe, "")]
    [InlineData(Exe, "\t")]
    public void RefusesToBuildATaskThatCouldNotRun(string exe, string user) =>
        Assert.Throws<ArgumentException>(() => AgentTask.BuildXml(exe, user));
}
