using OneRemoteCli.Daemon.Tray;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tests;

public sealed class TrayIconInteractionTests
{
    [Theory]
    [InlineData(NIN_SELECT, (int)TrayIconAction.DelayMenu)]
    [InlineData(NIN_KEYSELECT, (int)TrayIconAction.ShowMenu)]
    [InlineData(WM_CONTEXTMENU, (int)TrayIconAction.ShowMenu)]
    [InlineData(WM_LBUTTONDBLCLK, (int)TrayIconAction.ShowSettings)]
    [InlineData(WM_NULL, (int)TrayIconAction.None)]
    public void NotificationsMapToExpectedAction(int notification, int action) =>
        Assert.Equal((TrayIconAction)action, TrayIconInteraction.ActionFor(notification));
}
