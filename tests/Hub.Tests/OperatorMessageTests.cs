using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// What the operator actually reads on their phone.
/// <para>
/// Rendering is tested because these strings are the entire product of the feature —
/// there is no screen and no dashboard, and a number that formats as
/// <c>1.03:47:12.9981234</c> or an alert with a blank where a name should be is the whole
/// bug. The awkward cases are all boundaries: one of something, none of something, a
/// duration just under an hour, a byte count just over a kilobyte.
/// </para>
/// </summary>
public class OperatorMessageTests
{
    /// <summary>
    /// The most valuable message in the channel, so the exact string matters: it has to
    /// carry a command the operator can paste back without editing.
    /// </summary>
    [Fact]
    public void ARefusalCarriesTheCommandThatFixesIt()
    {
        string rendered = new OperatorMessage.AccountRefused(
            "newcomer@example.com",
            "tenant-guid:object-guid",
            RefusalKind.NotAllowlisted).Render();

        Assert.Contains("newcomer@example.com", rendered, StringComparison.Ordinal);
        Assert.Contains("/allow tenant-guid:object-guid", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the not-allowlisted case is fixed by <c>/allow</c>. Offering it for a missing
    /// scope would send the operator to change configuration that is already correct.
    /// </summary>
    [Theory]
    [InlineData(RefusalKind.MissingScope)]
    [InlineData(RefusalKind.NoUserKey)]
    public void ARefusalThatAllowingCannotFixDoesNotSuggestAllowing(RefusalKind kind)
    {
        string rendered = new OperatorMessage.AccountRefused("someone@example.com", "tenant:oid", kind).Render();

        Assert.DoesNotContain("/allow", rendered, StringComparison.Ordinal);
    }

    /// <summary>An account with no username is still an event, and must not render as a gap.</summary>
    [Fact]
    public void AnAccountWithNoUsernameStillReadsAsASentence()
    {
        string rendered = new OperatorMessage.AccountRefused(string.Empty, null, RefusalKind.NoUserKey).Render();

        Assert.Contains("(no username)", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart and a deploy are the same event from the outside, and the hub can only
    /// tell them apart by the version it recorded last time.
    /// </summary>
    [Fact]
    public void ADeployIsDistinguishedFromAPlainRestart()
    {
        Assert.Contains(
            "Deployed 1.4.0 (was 1.3.0)",
            new OperatorMessage.HubStarted("1.4.0", "1.3.0", 2, 1).Render(),
            StringComparison.Ordinal);

        Assert.Contains(
            "restarted",
            new OperatorMessage.HubStarted("1.4.0", "1.4.0", 2, 1).Render(),
            StringComparison.Ordinal);
    }

    /// <summary>The first start of a window has no previous version, and is not a deploy.</summary>
    [Fact]
    public void AFirstEverStartIsNotReportedAsADeploy()
    {
        Assert.False(new OperatorMessage.HubStarted("1.4.0", null, 1, 1).IsDeploy);
    }

    /// <summary>
    /// A digest that reports a partial week as a whole one is quietly wrong every time it
    /// happens, and App Service restarts often enough for that to be most weeks.
    /// </summary>
    [Fact]
    public void ADigestSaysSoWhenItDidNotWatchTheWholeWeek()
    {
        var from = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        string rendered = Digest(from, from.AddDays(7), observed: TimeSpan.FromDays(3), restarts: 4).Render();

        Assert.Contains("Covers 3d 0h of the 7d 0h week", rendered, StringComparison.Ordinal);
        Assert.Contains("43%", rendered, StringComparison.Ordinal);
        Assert.Contains("4 starts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ADigestThatDidWatchTheWholeWeekSaysThatInstead()
    {
        var from = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        string rendered = Digest(from, from.AddDays(7), observed: TimeSpan.FromDays(7), restarts: 1).Render();

        Assert.Contains("Covers the full week.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("%", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bytes-versus-characters distinction, made visible. The hub never decodes the
    /// stream, so a byte count is the only honest number it has.
    /// </summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(123456789, "118 MB")]
    public void BytesAreReportedAsBytes(long bytes, string expected)
    {
        Assert.Contains(
            expected,
            Digest(bytes: bytes).Render(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(5400, "1h 30m")]
    [InlineData(90000, "1d 1h")]
    public void DurationsReadAtAGlance(int seconds, string expected)
    {
        string rendered = new OperatorMessage.StatusReport(
            Machines: 1,
            Sessions: 1,
            Accounts: 1,
            Connections: 1,
            Uptime: TimeSpan.FromSeconds(seconds),
            Version: "1.0.0").Render();

        Assert.Contains($"Up {expected} on 1.0.0", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Singular and plural, because "1 machines connected" in the first line of the most
    /// frequently used command is the kind of thing that makes a tool feel unfinished.
    /// </summary>
    [Fact]
    public void CountsAgreeWithTheirNouns()
    {
        string one = new OperatorMessage.StatusReport(1, 1, 1, 1, TimeSpan.FromHours(1), "1.0.0").Render();
        string two = new OperatorMessage.StatusReport(2, 2, 2, 2, TimeSpan.FromHours(1), "1.0.0").Render();

        Assert.Contains("1 machine connected", one, StringComparison.Ordinal);
        Assert.Contains("1 live session", one, StringComparison.Ordinal);
        Assert.Contains("2 machines connected", two, StringComparison.Ordinal);
        Assert.Contains("2 live sessions", two, StringComparison.Ordinal);
    }

    /// <summary>An empty hub reports zero rather than saying nothing.</summary>
    [Fact]
    public void AnIdleHubStillReportsZeroes()
    {
        string rendered = new OperatorMessage.StatusReport(0, 0, 0, 0, TimeSpan.Zero, "1.0.0").Render();

        Assert.Contains("0 machines connected", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Push being unconfigured is the failure that is invisible from every other angle:
    /// sessions work, nothing errors, and notifications simply never arrive.
    /// </summary>
    [Fact]
    public void HealthShoutsWhenPushIsNotConfigured()
    {
        Assert.Contains(
            "Push NOT configured",
            new OperatorMessage.HealthReport("1.0.0", TimeSpan.FromDays(2), false, 3, 0).Render(),
            StringComparison.Ordinal);
    }

    /// <summary>The broadcast text is never echoed — it is reported as a length.</summary>
    [Fact]
    public void ABroadcastIsAcknowledgedByLengthNotByEcho()
    {
        string rendered = new OperatorMessage.BroadcastSent(4, 21).Render();

        Assert.Contains("21 characters", rendered, StringComparison.Ordinal);
        Assert.Contains("4 accounts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ABroadcastToNobodySaysSo()
    {
        Assert.Contains(
            "went nowhere",
            new OperatorMessage.BroadcastSent(0, 21).Render(),
            StringComparison.Ordinal);
    }

    /// <summary>An expired secret is a different sentence from one about to expire.</summary>
    [Fact]
    public void AnExpiredSecretIsReportedInThePastTense()
    {
        Assert.Contains(
            "has expired",
            new OperatorMessage.ClientSecretExpiring(0).Render(),
            StringComparison.Ordinal);

        Assert.Contains(
            "expires in 1 day.",
            new OperatorMessage.ClientSecretExpiring(1).Render(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every shape renders something. A message that reaches the queue and renders empty
    /// would be sent as a blank Telegram message, which the API rejects.
    /// </summary>
    [Fact]
    public void NoMessageRendersEmpty()
    {
        OperatorMessage[] every =
        [
            new OperatorMessage.AccountRefused("a@b.c", "t:o", RefusalKind.NotAllowlisted),
            new OperatorMessage.AccountFirstSeen("a@b.c", 1),
            new OperatorMessage.HubStarted("1.0.0", null, 1, 1),
            new OperatorMessage.AllowlistEmpty(),
            new OperatorMessage.PushFailuresSpiked(11, 4, 15),
            new OperatorMessage.TokenFailuresSpiked(11, 15),
            new OperatorMessage.AgentVersionSkew("0.9.0", "1.0.0", 1),
            new OperatorMessage.ClientSecretExpiring(7),
            Digest(),
            new OperatorMessage.StatusReport(1, 1, 1, 1, TimeSpan.FromHours(1), "1.0.0"),
            new OperatorMessage.HealthReport("1.0.0", TimeSpan.FromHours(1), true, 1, 1),
            new OperatorMessage.VersionReport("1.0.0"),
            new OperatorMessage.DigestRequested(),
            new OperatorMessage.AllowlistChanged("a@b.c", true, 2),
            new OperatorMessage.AccountKicked("a@b.c", 2),
            new OperatorMessage.BroadcastSent(1, 10),
            new OperatorMessage.CommandRejected(CommandFault.Unknown),
            new OperatorMessage.Help(),
        ];

        // Every declared shape is covered, so adding one without a test fails here.
        Assert.Equal(
            typeof(OperatorMessage).Assembly.GetTypes().Count(type => type.IsSubclassOf(typeof(OperatorMessage))),
            every.Length);

        Assert.All(every, message => Assert.False(string.IsNullOrWhiteSpace(message.Render())));
    }

    /// <summary>The help text states the rule, because that is where somebody will look for it.</summary>
    [Fact]
    public void HelpSaysWhatTheChannelWillNotTell()
    {
        Assert.Contains("counts only", new OperatorMessage.Help().Render(), StringComparison.OrdinalIgnoreCase);
    }

    private static OperatorMessage.WeeklyDigest Digest(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        long bytes = 1024,
        TimeSpan? observed = null,
        int restarts = 1)
    {
        DateTimeOffset start = from ?? new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = to ?? start.AddDays(7);

        return new OperatorMessage.WeeklyDigest(
            From: start,
            To: end,
            Sessions: 12,
            Bytes: bytes,
            Duration: TimeSpan.FromHours(9),
            ActiveAccounts: 2,
            NewAccounts: [],
            TopAccounts: [],
            Cost: 13m,
            Currency: "$",
            Observed: observed ?? (end - start),
            Restarts: restarts);
    }
}
