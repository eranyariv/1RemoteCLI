using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// How hard the agent tries to decide that a session is waiting for you, and how
/// long it waits before saying so.
/// <para>
/// Every number here is a guess about somebody else's tools. Eight seconds is right
/// for a coding agent that pauses to ask permission and wrong for a linker; the
/// patterns that identify a prompt differ between two versions of the same CLI. So
/// none of it is compiled in: the settings file is read at startup, and the
/// environment overrides it, because the person who needs to change this is the
/// person being interrupted, not the person who can rebuild.
/// </para>
/// </summary>
public sealed partial record AwaitingInputOptions
{
    /// <summary>Silence this long, with the screen in the right shape, means waiting.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The quiet period for a coding agent, which is much longer.
    /// <para>
    /// Eight seconds is a good reading of a shell, where silence means the command
    /// finished and the prompt is back. It is a bad reading of an agent, which stops
    /// printing for as long as the model takes to answer and leaves a cursor sitting
    /// in its input box the whole time — a screen indistinguishable, by the rules
    /// above, from one waiting for a person. The result was a notification per
    /// thinking pause, none of which the user could act on.
    /// </para>
    /// <para>
    /// Long enough that an agent still working is very unlikely to be this silent,
    /// short enough that one genuinely waiting is reported while the user still
    /// cares. It errs quiet: a late notification is a delay, an untrue one costs the
    /// feature.
    /// </para>
    /// </summary>
    public TimeSpan AgentQuietPeriod { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A session younger than this is never flagged.
    /// <para>
    /// Programs are quiet while they start. Announcing that a session which has
    /// existed for two seconds is waiting for input trains the user to disbelieve the
    /// next one, which is the only real currency this feature has.
    /// </para>
    /// </summary>
    public TimeSpan MinimumUptime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the screens are examined. Cheap: a few field reads per session.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Patterns that mean "waiting" without waiting for the quiet period.
    /// <para>
    /// Empty by default. The heuristic is deliberately primary: prompt wording varies
    /// per tool and per release, so a shipped pattern list would rot, and a pattern
    /// that stops matching fails silently. These exist for the user who knows exactly
    /// what their tool prints and wants to be told sooner.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PromptPatterns { get; init; } = [];

    /// <summary>
    /// These options as they apply to one session.
    /// <para>
    /// The only thing that varies is how long silence has to last, and it varies by
    /// what is running: see <see cref="AgentQuietPeriod"/>.
    /// </para>
    /// </summary>
    public AwaitingInputOptions ForCliType(CliType cliType) =>
        cliType is CliType.ClaudeCode or CliType.CopilotCli
            ? this with { QuietPeriod = AgentQuietPeriod }
            : this;

    public static AwaitingInputOptions Default { get; } = new();

    /// <summary>Where the settings file lives when the user has not said otherwise.</summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1RemoteCLI",
        "settings.json");

    /// <summary>
    /// Reads the settings file if there is one, then applies environment overrides.
    /// <para>
    /// Never throws. A malformed settings file must not stop the agent from running:
    /// the user would lose every session on the machine over a stray comma, and the
    /// thing they lost is worth far more than the setting they were editing.
    /// </para>
    /// </summary>
    public static AwaitingInputOptions Load(
        string? path = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? log = null)
    {
        AwaitingInputOptions options = ReadFile(path ?? SettingsPath, log);
        return ApplyEnvironment(options, environment, log);
    }

    private static AwaitingInputOptions ReadFile(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            SettingsFile? file = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                SettingsFileJson.Default.SettingsFile);

            AwaitingInputSettings? settings = file?.AwaitingInput;
            if (settings is null)
            {
                return Default;
            }

            return Default with
            {
                QuietPeriod = Seconds(settings.QuietPeriodSeconds) ?? Default.QuietPeriod,
                AgentQuietPeriod = Seconds(settings.AgentQuietPeriodSeconds) ?? Default.AgentQuietPeriod,
                MinimumUptime = Seconds(settings.MinimumUptimeSeconds) ?? Default.MinimumUptime,
                PollInterval = Seconds(settings.PollIntervalSeconds) ?? Default.PollInterval,
                PromptPatterns = settings.PromptPatterns ?? Default.PromptPatterns,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"settings: ignoring {path} ({ex.Message}).");
            return Default;
        }
    }

    private static AwaitingInputOptions ApplyEnvironment(
        AwaitingInputOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        Action<string>? log)
    {
        string? Read(string name) =>
            environment is null
                ? Environment.GetEnvironmentVariable(name)
                : environment.TryGetValue(name, out string? value)
                    ? value
                    : null;

        TimeSpan? Override(string name, TimeSpan current)
        {
            string? raw = Read(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return current;
            }

            if (double.TryParse(raw, out double seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            log?.Invoke($"settings: ignoring {name}='{raw}'.");
            return current;
        }

        return options with
        {
            QuietPeriod = Override("ONEREMOTE_QUIET_PERIOD_SECONDS", options.QuietPeriod)!.Value,
            AgentQuietPeriod = Override("ONEREMOTE_AGENT_QUIET_PERIOD_SECONDS", options.AgentQuietPeriod)!.Value,
            MinimumUptime = Override("ONEREMOTE_MINIMUM_UPTIME_SECONDS", options.MinimumUptime)!.Value,
        };
    }

    private static TimeSpan? Seconds(double? value) =>
        value is > 0 ? TimeSpan.FromSeconds(value.Value) : null;

    /// <summary>
    /// Compiles the user's patterns, dropping any that will not compile.
    /// <para>
    /// A bad pattern costs the user that one pattern, not the feature and not the
    /// agent. It is logged, because a regex that silently never matches is
    /// indistinguishable from a heuristic that never fires.
    /// </para>
    /// </summary>
    public IReadOnlyList<Regex> CompilePatterns(Action<string>? log = null)
    {
        if (PromptPatterns.Count == 0)
        {
            return [];
        }

        var compiled = new List<Regex>(PromptPatterns.Count);

        foreach (string pattern in PromptPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                // Timed out rather than trusted: these come from a file the user edits,
                // and a catastrophically backtracking pattern would otherwise stall the
                // sweep for every session on the machine.
                compiled.Add(new Regex(
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(50)));
            }
            catch (ArgumentException ex)
            {
                log?.Invoke($"settings: ignoring prompt pattern '{pattern}' ({ex.Message}).");
            }
        }

        return compiled;
    }

    private sealed class SettingsFile
    {
        [JsonPropertyName("awaitingInput")]
        public AwaitingInputSettings? AwaitingInput { get; set; }
    }

    private sealed class AwaitingInputSettings
    {
        public double? QuietPeriodSeconds { get; set; }

        public double? AgentQuietPeriodSeconds { get; set; }

        public double? MinimumUptimeSeconds { get; set; }

        public double? PollIntervalSeconds { get; set; }

        public List<string>? PromptPatterns { get; set; }
    }

    /// <summary>
    /// How the settings file is read.
    /// <para>
    /// Source-generated rather than reflection-based, so the shapes below survive
    /// trimming (issue #46). Without it the trimmer has no way to see that these
    /// properties are read by name, and the first thing a user would notice is their
    /// settings file being silently ignored.
    /// </para>
    /// <para>
    /// The three options are what make this file hand-editable: comments and a
    /// trailing comma are what somebody writes when they are commenting a setting out,
    /// and case-insensitivity forgives the capitalisation nobody remembers.
    /// </para>
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true)]
    [JsonSerializable(typeof(SettingsFile))]
    private sealed partial class SettingsFileJson : JsonSerializerContext;
}
