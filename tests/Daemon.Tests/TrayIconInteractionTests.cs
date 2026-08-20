using OneRemoteCli.Daemon.Tray;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tests;

public sealed class TrayIconInteractionTests
{
    [Theory]
    [InlineData(NIN_SELECT)]
    [InlineData(NIN_KEYSELECT)]
    [InlineData(WM_CONTEXTMENU)]
    public void ActivationOpensTheMenu(int notification) =>
        Assert.True(TrayIconInteraction.OpensMenu(notification));

    [Theory]
    [InlineData(WM_LBUTTONDBLCLK)]
    [InlineData(WM_NULL)]
    public void OtherNotificationsDoNotOpenTheMenu(int notification) =>
        Assert.False(TrayIconInteraction.OpensMenu(notification));
}
