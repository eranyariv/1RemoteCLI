using OneRemoteCli.Daemon.Shell;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Reading and writing real <c>.lnk</c> files.
/// <para>
/// Against the shell rather than a fake, because there is nothing here worth testing
/// except whether the COM calls land where they are meant to. The shell returns
/// success from a getter that was invoked through the wrong vtable slot and simply
/// writes nothing into the buffer, so a mis-declared interface produces a shortcut
/// that reads back as empty and a wrapped shortcut that points nowhere.
/// </para>
/// </summary>
public sealed class ShellLinkTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "1remote-shelllink-" + Guid.NewGuid().ToString("n"));

    public ShellLinkTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Path_(string name) => Path.Combine(_scratch, name);

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        string link = Path_("everything.lnk");

        var written = new ShellLinkInfo(
            Environment.ProcessPath!,
            "--name \"My Tool\" -- \"C:\\tools\\thing.exe\" --flag",
            _scratch,
            Environment.ProcessPath!,
            0,
            "A comment");

        ShellLink.Write(link, written);

        ShellLinkInfo read = ShellLink.Read(link);

        Assert.Equal(written.Target, read.Target, ignoreCase: true);
        Assert.Equal(written.Arguments, read.Arguments);
        Assert.Equal(written.WorkingDirectory, read.WorkingDirectory, ignoreCase: true);
        Assert.Equal(written.Description, read.Description);
        Assert.False(read.RunAsAdministrator);
    }

    [Fact]
    public void AShortcutWithNoArgumentsReadsBackWithNone()
    {
        // The getter returns S_FALSE and writes nothing at all for an empty field. A
        // buffer that was not cleared first would hand back whatever the previous call
        // left in it, which is how a shortcut acquires somebody else's arguments.
        string link = Path_("bare.lnk");

        ShellLink.Write(link, new ShellLinkInfo(Environment.ProcessPath!, "--something --long"));
        ShellLink.Write(link, new ShellLinkInfo(Environment.ProcessPath!));

        ShellLinkInfo read = ShellLink.Read(link);

        Assert.Equal(string.Empty, read.Arguments);
        Assert.True(read.HasProgram);
    }

    [Fact]
    public void QuotingSurvivesExactly()
    {
        // The arguments are copied through as one already-quoted string, so anything
        // the shell does to them on the way in and out is a change to what the child
        // is asked to run.
        const string Awkward = "-c \"echo 'hi there'\" --path \"C:\\Program Files\\x\" --empty \"\"";

        string link = Path_("quoting.lnk");

        ShellLink.Write(link, new ShellLinkInfo(Environment.ProcessPath!, Awkward));

        Assert.Equal(Awkward, ShellLink.Read(link).Arguments);
    }

    [Fact]
    public void AWrittenShortcutCanBeWrappedAndTheWrapPointsAtTheOriginal()
    {
        // The whole of issue #66 in one pass: read a shortcut the user has, plan a
        // wrap, write it, and read the result back the way Explorer would.
        string original = Path_("Claude Code.lnk");

        ShellLink.Write(
            original,
            new ShellLinkInfo(Environment.ProcessPath!, "--dangerously-skip", _scratch));

        string agent = Path.Combine(_scratch, "1remote.exe");

        WrapPlan plan = ShortcutWrapper.Plan(original, ShellLink.Read(original), agent);

        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(Path_("Claude Code (1Remote).lnk"), plan.OutputPath);

        ShellLink.Write(plan.OutputPath, plan.Link);

        ShellLinkInfo wrapped = ShellLink.Read(plan.OutputPath);

        Assert.Equal(agent, wrapped.Target, ignoreCase: true);
        Assert.StartsWith(
            "--name \"Claude Code\" --type generic -- ",
            wrapped.Arguments,
            StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessPath!, wrapped.Arguments, StringComparison.Ordinal);
        Assert.EndsWith("--dangerously-skip", wrapped.Arguments, StringComparison.Ordinal);
        Assert.Equal(_scratch, wrapped.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void WrappingTheWrappedShortcutIsRefused()
    {
        // Read back off disk rather than from the plan, because the check that matters
        // is the one made about a file somebody double-clicked on their desktop.
        string original = Path_("Tool.lnk");

        ShellLink.Write(original, new ShellLinkInfo(Environment.ProcessPath!));

        string agent = Path.Combine(_scratch, "1remote.exe");

        WrapPlan first = ShortcutWrapper.Plan(original, ShellLink.Read(original), agent);
        ShellLink.Write(first.OutputPath, first.Link);

        WrapPlan again = ShortcutWrapper.Plan(
            first.OutputPath,
            ShellLink.Read(first.OutputPath),
            agent);

        Assert.False(again.Ok);
        Assert.Contains("nest", again.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentVariableInTheTargetIsExpandedOnTheWayOut()
    {
        // A .lnk stores these unexpanded and CreateProcess expands nothing, so a target
        // read verbatim would be handed to the pseudoconsole as "%WINDIR%\..." and fail
        // as "the system cannot find the file specified".
        string link = Path_("expanded.lnk");

        ShellLink.Write(link, new ShellLinkInfo(@"%WINDIR%\System32\cmd.exe"));

        Assert.Equal(
            Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\cmd.exe"),
            ShellLink.Read(link).Target,
            ignoreCase: true);
    }
}
