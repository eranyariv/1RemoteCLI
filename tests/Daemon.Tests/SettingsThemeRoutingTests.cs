using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

public sealed class SettingsThemeRoutingTests
{
    [Theory]
    [InlineData(113, NotificationLevel.Off)]
    [InlineData(116, NotificationLevel.Off)]
    [InlineData(114, NotificationLevel.ActionRequired)]
    [InlineData(117, NotificationLevel.ActionRequired)]
    [InlineData(115, NotificationLevel.AllAttentionEvents)]
    [InlineData(118, NotificationLevel.AllAttentionEvents)]
    public void NotificationGlyphsAndLabelsSelectTheSameLevel(
        int control,
        NotificationLevel expected)
    {
        Assert.True(SettingsWindow.NotificationControlRouting.TryGetLevel(control, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(119)]
    public void OtherControlsDoNotSelectANotificationLevel(int control)
    {
        Assert.False(SettingsWindow.NotificationControlRouting.TryGetLevel(control, out _));
    }
}
