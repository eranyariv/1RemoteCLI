using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

public sealed class UpdateOptionsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "1remote-update-options",
        Guid.NewGuid().ToString("n"),
        "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not the subject.
        }
    }

    private UpdateOptions Load(string json, IReadOnlyDictionary<string, string?>? environment = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);

        return UpdateOptions.Load(_path, environment ?? new Dictionary<string, string?>());
    }

    [Fact]
    public void ChecksByDefault()
    {
        Assert.True(UpdateOptions.Default.Check);
        Assert.Equal(TimeSpan.FromHours(24), UpdateOptions.Default.Interval);
    }

    [Fact]
    public void UsesTheDefaultsWhenThereIsNoSettingsFile() =>
        Assert.Equal(
            UpdateOptions.Default,
            UpdateOptions.Load(_path, new Dictionary<string, string?>()));

    [Fact]
    public void UsesTheDefaultsWhenTheFileSaysNothingAboutUpdates() =>
        Assert.Equal(UpdateOptions.Default, Load("""{ "awaitingInput": { "enabled": true } }"""));

    [Fact]
    public void ReadsTheSettings()
    {
        UpdateOptions options = Load("""{ "update": { "check": false, "intervalHours": 6 } }""");

        Assert.False(options.Check);
        Assert.Equal(TimeSpan.FromHours(6), options.Interval);
    }

    [Fact]
    public void IgnoresAnIntervalThatIsNotATime() =>
        Assert.Equal(UpdateOptions.Default.Interval, Load("""{ "update": { "intervalHours": 0 } }""").Interval);

    /// <summary>
    /// Losing the agent over a stray comma would cost more than the setting was worth.
    /// </summary>
    [Fact]
    public void IgnoresAFileItCannotRead()
    {
        List<string> logged = [];

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");

        UpdateOptions options = UpdateOptions.Load(_path, new Dictionary<string, string?>(), logged.Add);

        Assert.Equal(UpdateOptions.Default, options);
        Assert.Contains(logged, line => line.Contains("ignoring", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowsTheCommentsAndTrailingCommasAPersonWillWrite()
    {
        UpdateOptions options = Load("""
            {
              // no thanks
              "update": { "check": false, },
            }
            """);

        Assert.False(options.Check);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" off ")]
    public void TheEnvironmentCanTurnCheckingOff(string value) =>
        Assert.False(Load("{}", new Dictionary<string, string?> { [UpdateOptions.EnvironmentVariable] = value }).Check);

    [Fact]
    public void TheEnvironmentCanTurnCheckingBackOn() =>
        Assert.True(Load(
            """{ "update": { "check": false } }""",
            new Dictionary<string, string?> { [UpdateOptions.EnvironmentVariable] = "1" }).Check);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyEnvironmentVariableSaysNothing(string value) =>
        Assert.False(Load(
            """{ "update": { "check": false } }""",
            new Dictionary<string, string?> { [UpdateOptions.EnvironmentVariable] = value }).Check);
}
