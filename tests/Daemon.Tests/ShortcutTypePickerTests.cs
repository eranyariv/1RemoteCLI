using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Tray;

namespace OneRemoteCli.Daemon.Tests;

[SupportedOSPlatform("windows")]
public sealed class ShortcutTypePickerTests
{
    [Fact]
    public void NativeStructuresUseTheCommonControlsPacking()
    {
        (int config, int button) = ShortcutTypePicker.NativeLayout;

        Assert.Equal(IntPtr.Size == 8 ? 160 : 96, config);
        Assert.Equal(4 + IntPtr.Size, button);
    }
}
