using OneRemoteCli.Daemon.Tray;

namespace OneRemoteCli.Daemon.Tests;

public sealed class SettingsWindowLayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "1remote-settings-layout-tests",
        Guid.NewGuid().ToString("n"));

    private string PathToLayout => Path.Combine(_directory, "settings-window.json");

    [Fact]
    public void RoundTripsEveryUserControlledLayoutChoice()
    {
        var expected = new SettingsWindowLayout
        {
            ClientWidth = 910,
            ClientHeight = 640,
            ActiveTab = 1,
            ColumnWidths = [210, 170, 310, 130, 155],
            SortColumn = SessionTableColumn.Folder,
            SortDescending = false,
        };
        var store = new SettingsWindowLayoutStore(PathToLayout);

        store.Save(expected);

        SettingsWindowLayout actual = store.Load();
        Assert.Equal(expected.ClientWidth, actual.ClientWidth);
        Assert.Equal(expected.ClientHeight, actual.ClientHeight);
        Assert.Equal(expected.ActiveTab, actual.ActiveTab);
        Assert.Equal(expected.ColumnWidths, actual.ColumnWidths);
        Assert.Equal(expected.SortColumn, actual.SortColumn);
        Assert.Equal(expected.SortDescending, actual.SortDescending);
    }

    [Fact]
    public void InvalidValuesAreClampedBeforeTheyReachWin32()
    {
        SettingsWindowLayout normalized = new SettingsWindowLayout
        {
            ClientWidth = -1,
            ClientHeight = int.MaxValue,
            ActiveTab = 99,
            ColumnWidths = [1],
            SortColumn = (SessionTableColumn)99,
        }.Normalize();

        Assert.Equal(SettingsWindowLayout.MinimumClientWidth, normalized.ClientWidth);
        Assert.Equal(SettingsWindowLayout.MaximumClientHeight, normalized.ClientHeight);
        Assert.Equal(2, normalized.ActiveTab);
        Assert.Equal(SettingsWindowLayout.DefaultColumnWidths, normalized.ColumnWidths);
        Assert.Equal(SessionTableColumn.Activity, normalized.SortColumn);
    }

    [Fact]
    public void MalformedStateFallsBackAndSaysWhy()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathToLayout, "{ definitely not json");
        var messages = new List<string>();

        SettingsWindowLayout loaded = new SettingsWindowLayoutStore(PathToLayout, messages.Add).Load();

        Assert.Equal(SettingsWindowLayout.Default.ClientWidth, loaded.ClientWidth);
        Assert.Equal(SettingsWindowLayout.Default.ClientHeight, loaded.ClientHeight);
        Assert.Equal(SettingsWindowLayout.Default.ColumnWidths, loaded.ColumnWidths);
        Assert.Single(messages);
        Assert.Contains("ignoring", messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SortsTextWithoutCaseChangingTheOrder()
    {
        SessionRow[] rows =
        [
            Row("zeta", "PowerShell", DateTimeOffset.Parse("2026-08-20T09:00:00Z")),
            Row("Alpha", "Claude Code chat", DateTimeOffset.Parse("2026-08-20T10:00:00Z")),
        ];

        IReadOnlyList<SessionRow> sorted =
            SessionTableSorter.Sort(rows, SessionTableColumn.Name, descending: false);

        Assert.Equal(["Alpha", "zeta"], sorted.Select(row => row.Name));
    }

    [Fact]
    public void MostRecentActivityIsTheDefaultDirection()
    {
        SessionRow[] rows =
        [
            Row("older", "PowerShell", DateTimeOffset.Parse("2026-08-20T09:00:00Z")),
            Row("newer", "GitHub Copilot chat", DateTimeOffset.Parse("2026-08-20T10:00:00Z")),
        ];

        IReadOnlyList<SessionRow> sorted =
            SessionTableSorter.Sort(rows, SessionTableColumn.Activity, descending: true);

        Assert.Equal(["newer", "older"], sorted.Select(row => row.Name));
    }

    private static SessionRow Row(string name, string source, DateTimeOffset activity) =>
        new(name, source, @"C:\source", "Running", "Started recently", activity);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
