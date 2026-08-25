using OneRemoteCli.Daemon.Tray;

namespace OneRemoteCli.Daemon.Tests;

public sealed class SettingsThemeRoutingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PhoneNotificationRadiosCustomDrawTheirText(int control)
    {
        Assert.True(SettingsWindow.SettingsThemeRouting.CustomDrawsRadioText(
            (IntPtr)control,
            (IntPtr)1,
            (IntPtr)2,
            (IntPtr)3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(100)]
    public void OtherControlsKeepTheirNativeTheme(int control)
    {
        Assert.False(SettingsWindow.SettingsThemeRouting.CustomDrawsRadioText(
            (IntPtr)control,
            (IntPtr)1,
            (IntPtr)2,
            (IntPtr)3));
    }
}
