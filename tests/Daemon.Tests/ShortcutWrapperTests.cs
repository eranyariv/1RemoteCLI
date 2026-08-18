using OneRemoteCli.Daemon.Shell;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Wrapping a shortcut the user already has (issue #66).
/// <para>
/// Almost every test here is a refusal, and that is the point. Each one describes a
/// shortcut that could be written successfully and would then fail on double-click,
/// in a way whose cause is completely invisible from the desktop — a Store app with
/// no program to run, an elevated shortcut whose child cannot reach a per-user pipe,
/// a second wrap nesting inside the first.
/// </para>
/// </summary>
public sealed class ShortcutWrapperTests
{
    private const string Agent = @"C:\Program Files\1RemoteCLI\1remote.exe";

    private static WrapPlan Plan(
        ShellLinkInfo source,
        string sourcePath = @"C:\Users\ada\Desktop\Claude Code.lnk",
        string? outputPath = null,
        Func<string, bool>? exists = null,
        ProgramKind kind = ProgramKind.Console) =>
        ShortcutWrapper.Plan(sourcePath, source, Agent, outputPath, exists ?? (_ => false), _ => kind);

    private static ShellLinkInfo Source(
        string target = @"C:\tools\claude\claude.cmd",
        string arguments = "",
        string workingDirectory = @"C:\work",
        bool elevated = false) =>
        new(target, arguments, workingDirectory, RunAsAdministrator: elevated);

    [Fact]
    public void AStoreShortcutIsRefusedByName()
    {
        // An AppUserModelID and no file. There is nothing to hand to a pseudoconsole,
        // and the message has to say what to wrap instead or the user has no next step.
        WrapPlan plan = Plan(new ShellLinkInfo(string.Empty));

        Assert.False(plan.Ok);
        Assert.Contains("Store", plan.Problem!, StringComparison.Ordinal);
        Assert.Contains(".exe", plan.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElevatedShortcutIsRefusedWithTheReason()
    {
        // The agent is per-user and unelevated, and its pipe is ACL'd to that user. An
        // elevated child would launch and then report the agent as missing.
        WrapPlan plan = Plan(Source(elevated: true));

        Assert.False(plan.Ok);
        Assert.Contains("administrator", plan.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unelevated", plan.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAlreadyWrappedShortcutIsNotWrappedAgain()
    {
        // Nesting a session inside a session. It would even work, in the sense of
        // starting, and produce two entries on the phone for one terminal.
        WrapPlan plan = Plan(
            Source(target: @"C:\Program Files\1RemoteCLI\1remote.exe", arguments: "--name \"x\" -- pwsh"),
            sourcePath: @"C:\Users\ada\Desktop\Claude Code (1Remote).lnk");

        Assert.False(plan.Ok);
        Assert.Contains("already starts a 1remote session", plan.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentIsRecognisedWhereverItWasInstalled()
    {
        // By file name, not full path: the same executable is wrapped from Program
        // Files, from a user's tools folder and from a build output, and a check on the
        // whole path would only ever catch one of them.
        WrapPlan plan = Plan(Source(target: @"D:\builds\debug\1remote.exe"));

        Assert.False(plan.Ok);
    }

    [Fact]
    public void TheWrappedShortcutRunsTheAgentAgainstTheOriginalProgram()
    {
        WrapPlan plan = Plan(Source(target: @"C:\Program Files\claude\claude.cmd", arguments: "--dangerously-skip"));

        Assert.True(plan.Ok);
        Assert.Equal(Agent, plan.Link.Target);

        // The original program is quoted because its path has a space in it. Getting
        // this wrong produces a session that tries to run "C:\Program".
        Assert.Equal(
            "--name \"Claude Code\" -- \"C:\\Program Files\\claude\\claude.cmd\" --dangerously-skip",
            plan.Link.Arguments);
    }

    [Fact]
    public void APathThatNeedsNoQuotingDoesNotGetAny()
    {
        // Quotes are added where they are needed, not everywhere. A command line full
        // of unnecessary quoting is one nobody can read back when it goes wrong.
        Assert.Equal(
            "--name \"Claude Code\" -- C:\\tools\\claude.cmd",
            Plan(Source(target: @"C:\tools\claude.cmd")).Link.Arguments);
    }

    [Fact]
    public void TheOriginalArgumentsGoThroughUntouched()
    {
        // Copied as the single string the shell stored rather than split and rejoined.
        // Re-quoting is two more chances to change what the program is asked to do.
        const string Awkward = "-c \"echo 'hi there'\" --path \"C:\\Program Files\\x\"";

        Assert.EndsWith(Awkward, Plan(Source(arguments: Awkward)).Link.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionIsNamedAfterTheShortcutNotTheProgram()
    {
        // "Claude Code" is what the user is looking for on their phone, not "node".
        Assert.Equal("Claude Code", Plan(Source()).DisplayName);
    }

    [Fact]
    public void TheWorkingDirectoryIsKept()
    {
        // For a great many tools it is the only reason they work at all.
        Assert.Equal(@"C:\work", Plan(Source(workingDirectory: @"C:\work")).Link.WorkingDirectory);
    }

    [Fact]
    public void AShortcutWithNoWorkingDirectoryGetsTheProgramsOwnFolder()
    {
        // Empty means "inherit", and what would be inherited is Explorer's, which is
        // unpredictable and occasionally C:\Windows\System32.
        Assert.Equal(
            @"C:\tools\claude",
            Plan(Source(workingDirectory: string.Empty)).Link.WorkingDirectory);
    }

    [Fact]
    public void TheIconIsCopiedSoTheUserStillRecognisesIt()
    {
        // A desktop full of identical 1remote icons is a feature nobody uses.
        Assert.Equal(@"C:\tools\claude\claude.cmd", Plan(Source()).Link.IconPath);
    }

    [Fact]
    public void ACollisionGetsANumberRatherThanOverwriting()
    {
        WrapPlan plan = Plan(
            Source(),
            exists: path => path.EndsWith(@"Claude Code (1Remote).lnk", StringComparison.Ordinal));

        Assert.True(plan.Ok);
        Assert.Equal(@"C:\Users\ada\Desktop\Claude Code (1Remote) (2).lnk", plan.OutputPath);
    }

    [Fact]
    public void TheSuffixIsNeverDoubled()
    {
        // Wrapping something that was already named "X (1Remote)" must not produce
        // "X (1Remote) (1Remote).lnk", which is how a name grows every time.
        Assert.Equal("Claude Code", ShortcutWrapper.NameOf(@"C:\x\Claude Code (1Remote).lnk"));
        Assert.Equal("Claude Code", ShortcutWrapper.NameOf(@"C:\x\Claude Code (1Remote) (1Remote).lnk"));
    }

    [Fact]
    public void RunningOutOfNamesIsSaidRatherThanLoopingForever()
    {
        WrapPlan plan = Plan(Source(), exists: _ => true);

        Assert.False(plan.Ok);
        Assert.Contains("--output", plan.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitOutputPathIsUsedAsGiven()
    {
        // The escape hatch the refusal above points at, so it has to bypass the naming
        // rule entirely rather than being another candidate for it.
        WrapPlan plan = Plan(Source(), outputPath: @"D:\elsewhere\thing.lnk", exists: _ => true);

        Assert.True(plan.Ok);
        Assert.Equal(@"D:\elsewhere\thing.lnk", plan.OutputPath);
    }

    [Fact]
    public void AWindowedProgramIsWarnedAboutButStillWrapped()
    {
        // A warning, not a refusal: the classification reads a PE header, and being
        // wrong about somebody's tool must not stop them using it.
        WrapPlan plan = Plan(Source(), kind: ProgramKind.Graphical);

        Assert.True(plan.Ok);
        Assert.NotNull(plan.Warning);
        Assert.Contains("empty terminal", plan.Warning!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProgramKind.Console)]
    [InlineData(ProgramKind.Unknown)]
    public void AnythingElseIsWrappedSilently(ProgramKind kind)
    {
        // Unknown deliberately says nothing. "We could not read your program" is a
        // warning nobody can act on, and those train people to dismiss the ones that
        // matter.
        Assert.Null(Plan(Source(), kind: kind).Warning);
    }

    [Fact]
    public void ABatchFileIsAConsoleProgramWithoutReadingAHeader()
    {
        // It has no PE header at all, and is run by a console host regardless of what
        // interprets it.
        Assert.Equal(ProgramKind.Console, ShortcutWrapper.Classify(@"C:\tools\claude.cmd"));
    }

    [Fact]
    public void SomethingThatIsNotThereIsUnknownRatherThanAGuess()
    {
        Assert.Equal(
            ProgramKind.Unknown,
            ShortcutWrapper.Classify(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.exe")));
    }

    [Fact]
    public void TheRunningTestHostIsSeenAsAConsoleProgram()
    {
        // Reads a real PE header, which is the part of the classifier that is arithmetic
        // over file offsets and therefore the part most likely to be quietly wrong.
        Assert.Equal(ProgramKind.Console, ShortcutWrapper.Classify(Environment.ProcessPath!));
    }
}
