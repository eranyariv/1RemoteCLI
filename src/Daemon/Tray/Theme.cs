using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>
/// The colours, fonts and window attributes that make a hand-built Win32 window look
/// like it belongs on Windows 11.
/// <para>
/// Everything here is the operating system's own theming, reached through DWM and
/// uxtheme, rather than a UI framework. That is the whole point: WinUI 3 was measured
/// at 166 MB self-contained against the 20 MB the agent ships today, and it would not
/// have replaced the tray icon or its menu anyway, because WinUI has neither
/// (issue #105). What follows costs nothing to download.
/// </para>
/// <para>
/// Every call degrades rather than fails. A Windows 10 machine has no Mica and no
/// rounded corners, and simply keeps the square, solid window it has now; a machine
/// whose uxtheme does not expose the menu entries keeps a light menu. None of it is
/// worth a failed dialog.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Theme : IDisposable
{
    /// <summary>Where Windows keeps the light/dark choice made in Settings.</summary>
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightTheme = "AppsUseLightTheme";

    /// <summary>
    /// Windows 11. Rounded corners arrived here; before it, asking for them is a no-op
    /// and asking for a backdrop is refused.
    /// </summary>
    private const int Windows11 = 22000;

    /// <summary>
    /// Fluent's <c>SolidBackgroundFillColorBase</c>, as COLORREF (0x00BBGGRR).
    /// <para>
    /// Not Mica. Mica needs the client area left unpainted so the compositor shows
    /// through, and the text over it would then be drawn by GDI with ClearType against
    /// an unknown backdrop, which fringes. WinUI itself falls back to exactly this
    /// colour when a backdrop is unavailable, so this is the supported appearance
    /// rather than an approximation of one.
    /// </para>
    /// </summary>
    private const uint DarkSurface = 0x00202020;

    private const uint LightSurface = 0x00F3F3F3;

    /// <summary>Fluent's <c>LayerFillColorDefault</c>: the list box, one step off the surface.</summary>
    private const uint DarkLayer = 0x002B2B2B;

    private const uint LightLayer = 0x00FBFBFB;

    /// <summary>Fluent's <c>TextFillColorPrimary</c>.</summary>
    private const uint DarkText = 0x00FFFFFF;

    private const uint LightText = 0x001B1B1B;

    /// <summary>
    /// <c>TextFillColorSecondary</c>, for the things that are true but not why the
    /// window was opened: the version, the empty-list sentence.
    /// </summary>
    private const uint DarkSecondaryText = 0x00C5C5C5;

    private const uint LightSecondaryText = 0x005D5D5D;

    /// <summary>Fluent's <c>CardStrokeColorDefault</c>: the hairline around the list.</summary>
    private const uint DarkBorder = 0x00383838;

    private const uint LightBorder = 0x00E5E5E5;

    private Theme(bool dark)
    {
        Dark = dark;

        Surface = dark ? DarkSurface : LightSurface;
        Layer = dark ? DarkLayer : LightLayer;
        Text = dark ? DarkText : LightText;
        SecondaryText = dark ? DarkSecondaryText : LightSecondaryText;
        Border = dark ? DarkBorder : LightBorder;

        SurfaceBrush = CreateSolidBrush(Surface);
        LayerBrush = CreateSolidBrush(Layer);
        BorderBrush = CreateSolidBrush(Border);
    }

    internal bool Dark { get; }

    internal uint Surface { get; }

    internal uint Layer { get; }

    internal uint Text { get; }

    internal uint SecondaryText { get; }

    internal uint Border { get; }

    internal IntPtr SurfaceBrush { get; }

    internal IntPtr LayerBrush { get; }

    internal IntPtr BorderBrush { get; }

    /// <summary>
    /// The current system preference, as a theme.
    /// <para>
    /// Read every time rather than cached, because the window is told to re-read it on
    /// <c>WM_SETTINGCHANGE</c>. A missing or unreadable value means light: that is what
    /// Windows itself falls back to, and guessing dark on a light desktop is the more
    /// visible error.
    /// </para>
    /// </summary>
    internal static Theme Current()
    {
        object? value = null;

        try
        {
            value = Registry.GetValue($@"HKEY_CURRENT_USER\{PersonalizeKey}", AppsUseLightTheme, 1);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Left null, which is light.
        }

        return new Theme(value is int light && light == 0);
    }

    /// <summary>
    /// Puts the caption bar, the corners and the frame in the right theme.
    /// <para>
    /// The caption is the one part of a window an application cannot paint, so a dark
    /// window with a white title bar is the single most obvious way of looking like it
    /// was never updated. Rounded corners and the border colour are the other two
    /// things Windows 11 does that a window created with default styles does not get.
    /// </para>
    /// </summary>
    internal void ApplyToWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        int dark = Dark ? 1 : 0;
        Set(window, DWMWA_USE_IMMERSIVE_DARK_MODE, dark);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11))
        {
            return;
        }

        Set(window, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // Explicit rather than left to the system: the default border of a dark window
        // on Windows 11 is the light one, because DWM decides before we have said which
        // theme this window is in.
        Set(window, DWMWA_BORDER_COLOR, unchecked((int)Border));

        static void Set(IntPtr window, int attribute, int value)
        {
            // A refusal is a refusal to look nicer. Windows returns E_INVALIDARG for an
            // attribute this build does not know, which is exactly the case the version
            // checks above are approximating and not worth a second failure path.
            _ = DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));
        }
    }

    /// <summary>
    /// Hands a control to the dark variant of the Explorer theme.
    /// <para>
    /// This is what makes a stock <c>BUTTON</c> or <c>LISTBOX</c> draw itself dark:
    /// comctl32 has the artwork, but only uses it when the control has been told which
    /// theme class it belongs to. Nothing here draws anything by hand.
    /// </para>
    /// <para>
    /// Passing null in light mode is not the same as doing nothing — it puts a control
    /// that had been made dark back on the default theme, which is what a live switch
    /// from dark to light needs.
    /// </para>
    /// </summary>
    internal void ApplyToControl(IntPtr control)
    {
        if (control != IntPtr.Zero)
        {
            _ = SetWindowTheme(control, Dark ? "DarkMode_Explorer" : null, null);
        }
    }

    /// <summary>
    /// Lets popup menus follow the system theme.
    /// <para>
    /// The tray menu is the surface every user sees and the only one that appears over
    /// the taskbar, where a white rectangle on a dark shell is conspicuous. Windows
    /// draws it, not us, and the switch that decides which way is not in any header:
    /// uxtheme exports it by ordinal only. So it is looked up by number, and if it is
    /// not there — a future Windows that dropped it, a build that never had it — the
    /// menu simply stays as it is today.
    /// </para>
    /// <para>
    /// <c>AllowDark</c> rather than <c>ForceDark</c>: it asks Windows to apply the
    /// user's own preference, so the menu changes with the system instead of being
    /// pinned dark by us.
    /// </para>
    /// </summary>
    internal static unsafe void AllowSystemThemedMenus()
    {
        const int SetPreferredAppModeOrdinal = 135;
        const int FlushMenuThemesOrdinal = 136;
        const int RefreshImmersiveColorPolicyStateOrdinal = 104;
        const int AllowDark = 1;

        IntPtr uxtheme = LoadLibrary("uxtheme.dll");

        if (uxtheme == IntPtr.Zero)
        {
            return;
        }

        IntPtr setPreferredAppMode = GetProcAddress(uxtheme, SetPreferredAppModeOrdinal);

        if (setPreferredAppMode == IntPtr.Zero)
        {
            return;
        }

        // Called through a function pointer rather than a marshalled delegate: there is
        // nothing to marshal, and it keeps this off the reflection paths that trimming
        // cannot see through.
        ((delegate* unmanaged[Stdcall]<int, int>)setPreferredAppMode)(AllowDark);

        IntPtr refresh = GetProcAddress(uxtheme, RefreshImmersiveColorPolicyStateOrdinal);

        if (refresh != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<void>)refresh)();
        }

        // Without this the change applies to menus created after the next theme change
        // rather than to the next menu, which reads as the setting not working.
        IntPtr flush = GetProcAddress(uxtheme, FlushMenuThemesOrdinal);

        if (flush != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<void>)flush)();
        }
    }

    /// <summary>
    /// Whether this Windows has Segoe UI Variable, the typeface Windows 11 is set in.
    /// <para>
    /// Windows 10 does not, and asking for a font that is not installed does not fail —
    /// GDI silently substitutes something that is, which on this call is not Segoe UI.
    /// So it is chosen by version rather than by asking.
    /// </para>
    /// </summary>
    internal static string BodyFace =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11) ? "Segoe UI Variable Text" : "Segoe UI";

    public void Dispose()
    {
        Delete(SurfaceBrush);
        Delete(LayerBrush);
        Delete(BorderBrush);

        static void Delete(IntPtr brush)
        {
            if (brush != IntPtr.Zero)
            {
                DeleteObject(brush);
            }
        }
    }
}
