using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Who this machine is, from the product's point of view.
/// <para>
/// The identifier is a GUID generated on first run, not the computer name.
/// Computer names collide (every fresh Windows install is full of DESKTOP-XXXXXXX),
/// they change when a machine is renamed or re-imaged, and a user can set one to
/// anything they like — so they are useless as a key and dangerous as a claim. The
/// computer name is kept only as the default <see cref="DisplayName"/>, which is
/// what the phone shows and what the user may rename freely.
/// </para>
/// </summary>
public sealed partial class MachineIdentity
{
    private const string FolderName = "1RemoteCLI";
    private const string FileName = "machine.json";

    [JsonConstructor]
    public MachineIdentity(string machineId, string displayName)
    {
        MachineId = machineId;
        DisplayName = displayName;
    }

    /// <summary>Stable GUID for this machine, in "N" form.</summary>
    public string MachineId { get; }

    /// <summary>Friendly label shown on the phone. Defaults to the computer name.</summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Where the identity lives: <c>%LOCALAPPDATA%\1RemoteCLI\machine.json</c>.
    /// Local (not roaming) because the identity is deliberately per machine — a
    /// roamed file would give two machines the same id, which is exactly the
    /// confusion the GUID exists to prevent.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName,
        FileName);

    /// <summary>
    /// Loads the identity, creating it on first run.
    /// <para>
    /// A file we cannot parse is replaced rather than treated as fatal. The identity
    /// is not recoverable from anywhere else, so refusing to start would leave the
    /// user with an agent that never runs again and no obvious way to fix it; taking
    /// a new id costs them a re-pair of one machine.
    /// </para>
    /// </summary>
    public static MachineIdentity Load(string? path = null, Action<string>? log = null)
    {
        path ??= DefaultPath;

        if (File.Exists(path))
        {
            try
            {
                MachineIdentity? loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    MachineIdentityJson.Default.MachineIdentity);

                if (loaded is not null && Guid.TryParse(loaded.MachineId, out _) &&
                    !string.IsNullOrWhiteSpace(loaded.DisplayName))
                {
                    return loaded;
                }

                log?.Invoke($"1remote: {path} is not a valid machine identity; generating a new one.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"1remote: could not read {path} ({ex.Message}); generating a new machine identity.");
            }
        }

        var identity = new MachineIdentity(Guid.NewGuid().ToString("n"), Environment.MachineName);
        identity.Save(path);
        return identity;
    }

    /// <summary>
    /// Writes the identity through a temporary file, so a crash mid-write cannot
    /// leave a half-written file that would cost the machine its identity.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The identity path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, MachineIdentityJson.Default.MachineIdentity));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// How the identity file is written and read.
    /// <para>
    /// Source-generated rather than reflection-based, so it survives trimming (issue
    /// #46). This one matters more than most: a trimmed build that could not read the
    /// file would silently generate a new identity, and the user's phone would find an
    /// unfamiliar machine and none of their sessions.
    /// </para>
    /// <para>
    /// camelCase and case-insensitive reads because this file is meant to be opened
    /// and hand-edited by the user renaming their machine.
    /// </para>
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(MachineIdentity))]
    private sealed partial class MachineIdentityJson : JsonSerializerContext;
}
