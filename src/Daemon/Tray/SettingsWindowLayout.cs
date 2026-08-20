using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneRemoteCli.Daemon.Tray;

public enum SessionTableColumn
{
    Name,
    Source,
    Folder,
    Status,
    Activity,
}

public sealed record SettingsWindowLayout
{
    public const int MinimumClientWidth = 620;
    public const int MinimumClientHeight = 420;
    public const int MaximumClientWidth = 2400;
    public const int MaximumClientHeight = 1600;
    public const int MinimumColumnWidth = 60;
    public const int MaximumColumnWidth = 1200;

    public static readonly int[] DefaultColumnWidths = [185, 150, 220, 115, 130];

    public int ClientWidth { get; init; } = 900;

    public int ClientHeight { get; init; } = 520;

    public int ActiveTab { get; init; }

    public int[] ColumnWidths { get; init; } = [.. DefaultColumnWidths];

    public SessionTableColumn SortColumn { get; init; } = SessionTableColumn.Activity;

    public bool SortDescending { get; init; } = true;

    public static SettingsWindowLayout Default => new();

    public SettingsWindowLayout Normalize()
    {
        int[] widths = ColumnWidths is { Length: 5 }
            ? [.. ColumnWidths]
            : [.. DefaultColumnWidths];

        for (int index = 0; index < widths.Length; index++)
        {
            widths[index] = Math.Clamp(widths[index], MinimumColumnWidth, MaximumColumnWidth);
        }

        return this with
        {
            ClientWidth = Math.Clamp(ClientWidth, MinimumClientWidth, MaximumClientWidth),
            ClientHeight = Math.Clamp(ClientHeight, MinimumClientHeight, MaximumClientHeight),
            ActiveTab = Math.Clamp(ActiveTab, 0, 2),
            ColumnWidths = widths,
            SortColumn = Enum.IsDefined(SortColumn) ? SortColumn : SessionTableColumn.Activity,
        };
    }
}

internal static class SessionTableSorter
{
    public static IReadOnlyList<SessionRow> Sort(
        IEnumerable<SessionRow> rows,
        SessionTableColumn column,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(rows);

        IOrderedEnumerable<SessionRow> ordered = column switch
        {
            SessionTableColumn.Source => Order(rows, row => row.Source, descending),
            SessionTableColumn.Folder => Order(rows, row => row.Folder, descending),
            SessionTableColumn.Status => Order(rows, row => row.Status, descending),
            SessionTableColumn.Activity => descending
                ? rows.OrderByDescending(row => row.ActivityAt)
                : rows.OrderBy(row => row.ActivityAt),
            _ => Order(rows, row => row.Name, descending),
        };

        return
        [
            .. ordered
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Folder, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static IOrderedEnumerable<SessionRow> Order(
        IEnumerable<SessionRow> rows,
        Func<SessionRow, string> key,
        bool descending) =>
        descending
            ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);
}

internal sealed partial class SettingsWindowLayoutStore(
    string? path = null,
    Action<string>? log = null)
{
    private const string FileName = "settings-window.json";
    private readonly string _path = path ?? DefaultPath;
    private readonly Action<string>? _log = log;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1RemoteCLI",
        FileName);

    public SettingsWindowLayout Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return SettingsWindowLayout.Default;
            }

            SettingsWindowLayout? layout = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                LayoutJson.Default.SettingsWindowLayout);

            return (layout ?? SettingsWindowLayout.Default).Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke($"settings: ignoring {_path} ({ex.Message}).");
            return SettingsWindowLayout.Default;
        }
    }

    public void Save(SettingsWindowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The settings-window path has no directory.");
            Directory.CreateDirectory(directory);

            string temporary = _path + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(layout.Normalize(), LayoutJson.Default.SettingsWindowLayout));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"settings: could not save {_path} ({ex.Message}).");
        }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(SettingsWindowLayout))]
    private sealed partial class LayoutJson : JsonSerializerContext;
}
