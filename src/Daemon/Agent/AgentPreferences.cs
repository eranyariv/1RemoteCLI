using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Daemon.Agent;

internal sealed record AgentPreferences
{
    public bool HideArchivedSessions { get; init; } = true;

    public static AgentPreferences Default => new();
}

internal sealed partial class AgentPreferencesStore(
    string? path = null,
    Action<string>? log = null)
{
    private const string FileName = "agent-preferences.json";
    private readonly string _path = path ?? DefaultPath;
    private readonly Action<string>? _log = log;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1RemoteCLI",
        FileName);

    public AgentPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AgentPreferences.Default;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                PreferencesJson.Default.AgentPreferences) ?? AgentPreferences.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke($"settings: ignoring {_path} ({ex.Message}).");
            return AgentPreferences.Default;
        }
    }

    public string? Save(AgentPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The agent-preferences path has no directory.");
            Directory.CreateDirectory(directory);

            string temporary = _path + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(preferences, PreferencesJson.Default.AgentPreferences));
            File.Move(temporary, _path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"settings: could not save {_path} ({ex.Message}).");
            return "The archived-session preference could not be saved.";
        }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AgentPreferences))]
    private sealed partial class PreferencesJson : JsonSerializerContext;
}
