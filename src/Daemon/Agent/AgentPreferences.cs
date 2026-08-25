using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Daemon.Agent;

internal sealed record AgentPreferences
{
    public bool HideArchivedSessions { get; init; } = true;

    public bool AutomaticUpdates { get; init; } = true;

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

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Agent preferences must be a JSON object.");
            }

            AgentPreferences defaults = AgentPreferences.Default;
            return defaults with
            {
                HideArchivedSessions =
                    ReadBoolean(document.RootElement, "hideArchivedSessions")
                    ?? defaults.HideArchivedSessions,
                AutomaticUpdates =
                    ReadBoolean(document.RootElement, "automaticUpdates")
                    ?? defaults.AutomaticUpdates,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke($"settings: ignoring {_path} ({ex.Message}).");
            return AgentPreferences.Default;
        }
    }

    private static bool? ReadBoolean(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new JsonException($"Agent preference '{name}' must be true or false."),
            };
        }

        return null;
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
            return "The agent settings could not be saved.";
        }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AgentPreferences))]
    private sealed partial class PreferencesJson : JsonSerializerContext;
}
