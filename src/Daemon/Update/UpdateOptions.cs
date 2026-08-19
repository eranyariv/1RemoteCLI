using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Daemon.Update;

/// <summary>
/// Whether the agent looks for new releases, and how often.
/// <para>
/// Checking is on by default and installing is never automatic. The two are worth
/// separating: a machine that quietly replaces its own program file while somebody is
/// working on it is a machine its owner has stopped being able to reason about, and
/// the thing that actually goes stale is not the binary, it is the user's knowledge
/// that a newer one exists. So the agent finds out and says so, and a person decides.
/// </para>
/// <para>
/// Read from the same <c>settings.json</c> as everything else the user can tune, and
/// read the same way: anything unreadable is ignored rather than fatal, because losing
/// the agent over a stray comma costs more than the setting was worth.
/// </para>
/// </summary>
public sealed partial record UpdateOptions
{
    /// <summary>Whether to look for new releases at all.</summary>
    public bool Check { get; init; } = true;

    /// <summary>
    /// How long between checks.
    /// <para>
    /// A day. Releases here are days apart at their fastest, so anything shorter only
    /// adds requests to github.com from every machine running the agent — and issue
    /// #102 is what an exhausted allowance looks like from the other end.
    /// </para>
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after the agent starts before the first check.
    /// <para>
    /// Not immediately. Logon is the busiest moment a machine has, and the network is
    /// frequently not up yet — a check that runs at second zero mostly measures how
    /// long wifi took, and its failure would be the first thing the settings window
    /// said about a machine that is working perfectly.
    /// </para>
    /// </summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(2);

    public static UpdateOptions Default { get; } = new();

    public static string SettingsPath => Agent.AwaitingInputOptions.SettingsPath;

    public static UpdateOptions Load(
        string? path = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? log = null)
    {
        UpdateOptions options = ReadFile(path ?? SettingsPath, log);

        string? raw = environment is null
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : environment.TryGetValue(EnvironmentVariable, out string? value) ? value : null;

        return string.IsNullOrWhiteSpace(raw) ? options : options with { Check = !IsOff(raw) };
    }

    /// <summary>
    /// Turns checking off for one run, for a machine whose owner does not want it and
    /// has not got as far as the settings file.
    /// </summary>
    public const string EnvironmentVariable = "ONEREMOTE_UPDATE_CHECK";

    private static bool IsOff(string value) =>
        value.Trim() is "0" or "off" or "no"
        || string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static UpdateOptions ReadFile(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            UpdateSettings? settings = JsonSerializer
                .Deserialize(File.ReadAllText(path), UpdateSettingsJson.Default.SettingsFile)
                ?.Update;

            if (settings is null)
            {
                return Default;
            }

            return Default with
            {
                Check = settings.Check ?? Default.Check,
                Interval = Hours(settings.IntervalHours) ?? Default.Interval,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"settings: ignoring {path} ({ex.Message}).");
            return Default;
        }
    }

    private static TimeSpan? Hours(double? value) => value is > 0 ? TimeSpan.FromHours(value.Value) : null;

    private sealed class SettingsFile
    {
        [JsonPropertyName("update")]
        public UpdateSettings? Update { get; set; }
    }

    private sealed class UpdateSettings
    {
        public bool? Check { get; set; }

        public double? IntervalHours { get; set; }
    }

    /// <summary>
    /// Source-generated, so these shapes survive trimming (issue #46). Reflection-based
    /// serialization would leave the trimmer unable to see that the properties are read
    /// by name, and the symptom would be a settings file silently ignored.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true)]
    [JsonSerializable(typeof(SettingsFile))]
    private sealed partial class UpdateSettingsJson : JsonSerializerContext;
}
