using OneRemoteCli.Daemon.Diagnostics;
using OneRemoteCli.Daemon.Install;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The self-check exists to catch what this suite cannot, so these tests can only
/// cover the part that is testable: that the checks pass on a build that works, and
/// that a failure is reported as one. Whether they catch a trimmed build is settled
/// by the publish script running them against the real artifact.
/// </summary>
public sealed class SelfCheckTests
{
    [Fact]
    public void EveryCheckPassesOnABuildThatWorks()
    {
        IReadOnlyList<StepResult> checks = SelfCheck.Run();

        Assert.NotEmpty(checks);

        // The manifest check is the exception, and not because it is flaky. It asks what
        // the running executable was manifested as, and the running executable here is the
        // test host, which is not ours and carries no manifest of ours. Under the shipped
        // 1remote.exe it passes, which is where it is asked: the publish script runs the
        // whole self-check against the artifact it just produced.
        Assert.All(
            checks.Where(check => !check.Message.StartsWith(SelfCheck.ChromeCheckName, StringComparison.Ordinal)),
            check => Assert.True(check.Ok, check.Message));
    }

    [Fact]
    public void TheManifestIsChecked()
    {
        // Pinned by name because the check above deliberately excuses it, and an excused
        // check that quietly stopped being run would leave nothing watching the manifest.
        Assert.Contains(
            SelfCheck.Run(),
            check => check.Message.StartsWith(SelfCheck.ChromeCheckName, StringComparison.Ordinal));
    }

    [Fact]
    public void TheChecksCleanUpAfterThemselves()
    {
        // It runs on the user's machine as well as in the publish script, so leaving
        // scratch directories behind in %TEMP% would be a slow leak nobody attributes.
        string[] before = LeftBehind();

        _ = SelfCheck.Run();

        Assert.Equal(before, LeftBehind());

        static string[] LeftBehind() =>
            Directory.GetDirectories(Path.GetTempPath(), "1remote-selfcheck-*");
    }

    [Fact]
    public void APassingRunSaysTheBuildIsGood()
    {
        string summary = SelfCheck.Summarise([StepResult.Success("a"), StepResult.Success("b")]);

        Assert.Contains("works", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingRunSaysNotToShipIt()
    {
        string summary = SelfCheck.Summarise([StepResult.Success("a"), StepResult.Failure("b")]);

        Assert.Contains("1 check(s) failed", summary, StringComparison.Ordinal);
        Assert.Contains("Do not ship", summary, StringComparison.Ordinal);
    }
}
