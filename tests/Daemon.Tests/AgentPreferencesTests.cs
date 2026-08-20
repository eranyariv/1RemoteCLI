using OneRemoteCli.Daemon.Agent;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AgentPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "1remote-agent-preferences-tests",
        Guid.NewGuid().ToString("n"));

    private string PreferencesPath => Path.Combine(_directory, "agent-preferences.json");

    [Fact]
    public void HidesArchivedSessionsByDefault()
    {
        var store = new AgentPreferencesStore(PreferencesPath);

        AgentPreferences preferences = store.Load();

        Assert.True(preferences.HideArchivedSessions);
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void PersistsTheUsersChoice()
    {
        var store = new AgentPreferencesStore(PreferencesPath);

        string? problem = store.Save(new AgentPreferences { HideArchivedSessions = false });

        Assert.Null(problem);
        Assert.False(store.Load().HideArchivedSessions);
    }

    [Fact]
    public void MalformedPreferencesFallBackToCheckedAndSayWhy()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, "{ definitely not json");
        var messages = new List<string>();
        var store = new AgentPreferencesStore(PreferencesPath, messages.Add);

        AgentPreferences preferences = store.Load();

        Assert.True(preferences.HideArchivedSessions);
        Assert.Single(messages);
        Assert.Contains("ignoring", messages[0], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
