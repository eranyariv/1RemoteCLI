using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol.Hub;

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
        Assert.True(preferences.AutomaticUpdates);
        Assert.Equal(NotificationLevel.AllAttentionEvents, preferences.PhoneNotificationLevel);
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void PersistsTheUsersChoice()
    {
        var store = new AgentPreferencesStore(PreferencesPath);

        string? problem = store.Save(new AgentPreferences
        {
            HideArchivedSessions = false,
            AutomaticUpdates = false,
        });

        Assert.Null(problem);
        AgentPreferences saved = store.Load();
        Assert.False(saved.HideArchivedSessions);
        Assert.False(saved.AutomaticUpdates);
        Assert.Equal(NotificationLevel.AllAttentionEvents, saved.PhoneNotificationLevel);
    }

    [Fact]
    public void ExistingPreferencesEnableAutomaticUpdatesByDefault()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, """{ "hideArchivedSessions": false }""");
        var store = new AgentPreferencesStore(PreferencesPath);

        AgentPreferences preferences = store.Load();

        Assert.False(preferences.HideArchivedSessions);
        Assert.True(preferences.AutomaticUpdates);
        Assert.Equal(NotificationLevel.AllAttentionEvents, preferences.PhoneNotificationLevel);
    }

    [Theory]
    [InlineData(NotificationLevel.AllAttentionEvents)]
    [InlineData(NotificationLevel.ActionRequired)]
    [InlineData(NotificationLevel.Off)]
    public void PersistsEveryPhoneNotificationLevel(NotificationLevel level)
    {
        var store = new AgentPreferencesStore(PreferencesPath);

        Assert.Null(store.Save(new AgentPreferences { PhoneNotificationLevel = level }));

        Assert.Equal(level, store.Load().PhoneNotificationLevel);
    }

    [Fact]
    public void RefusesAnUnknownPhoneNotificationLevel()
    {
        var store = new AgentPreferencesStore(PreferencesPath);

        string? problem = store.Save(new AgentPreferences
        {
            PhoneNotificationLevel = (NotificationLevel)255,
        });

        Assert.NotNull(problem);
        Assert.False(File.Exists(PreferencesPath));
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
        Assert.True(preferences.AutomaticUpdates);
        Assert.Equal(NotificationLevel.AllAttentionEvents, preferences.PhoneNotificationLevel);
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
