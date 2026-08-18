using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>
/// Draws the tray icon: the product mark, wearing its connection status.
/// <para>
/// Split out from <see cref="TrayIcon"/> because the composition is the part worth
/// testing and the tray is the part that cannot be tested — a <c>NotifyIcon</c> needs
/// a desktop and a message loop, a bitmap needs neither. It also keeps the drawing
/// clear of Windows Forms, which issue #46 wants to remove.
/// </para>
/// <para>
/// Connected is drawn as the bare mark. Every other state carries a badge, so a
/// decorated tray means something wants attention. The cues are deliberately redundant:
/// the badge <em>shape</em> survives colour blindness — a bang inside a disc for
/// reconnecting, a barred circle for signed out — and the badge <em>colour</em> is the
/// fastest to read at a glance. Either can fail and the icon still answers the only
/// question it exists to answer: is this working.
/// </para>
/// <para>
/// Both of the things the icon has to say — how many sessions are live (issue #76) and
/// whether the hub can be reached (issue #77) — are carried by the artwork itself
/// rather than composited at run time. <c>assets/tray</c> holds a drawn variant for
/// every state and count, and <c>scripts/make-icons.ps1</c> turns each into its own
/// <c>.ico</c>. Picking a whole prepared image is what lets both survive 16 pixels; a
/// digit or a badge drawn into the corner at that size is a smudge whatever care goes
/// into it. So this class chooses a file and scales it, and nothing else.
/// </para>
/// <para>
/// The count is deliberately independent of the connection state: sessions keep running
/// while the hub is unreachable, so the number means the same thing in all three states
/// and is shown in all three.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayArtwork
{
    /// <summary>
    /// The embedded artwork, one file per state and count. <c>{0}</c> is the state,
    /// <c>{1}</c> is the count — <c>Base</c>, <c>1</c>..<c>9</c>, or <c>More</c>.
    /// </summary>
    internal const string ResourceFormat = "1RemoteCLI.Tray.{0}.{1}.ico";

    /// <summary>Matches the <c>LogicalName</c> the daemon project embeds the plain mark under.</summary>
    internal const string LogoResourceName = "1RemoteCLI.Tray.Connected.Base.ico";

    /// <summary>
    /// The first count drawn as <c>&gt;9</c> rather than as itself.
    /// <para>
    /// Two digits at 16 pixels is a handful of pixels of stroke per digit, which is
    /// mush. And nobody with ten live sessions is reading a tray icon to find out
    /// whether it is eleven — past a point the only fact left is "a lot", so that is
    /// what the artwork says.
    /// </para>
    /// </summary>
    public const int CountCeiling = 10;

    private static readonly Color Green = Color.FromArgb(0x3F, 0xB9, 0x50);
    private static readonly Color Amber = Color.FromArgb(0xE3, 0xB3, 0x41);
    private static readonly Color Grey = Color.FromArgb(0x9A, 0xA0, 0xA6);

    /// <summary>
    /// Builds the icon for one state at one size.
    /// <para>
    /// The caller owns the result and must dispose it.
    /// </para>
    /// </summary>
    /// <param name="state">What the agent is doing.</param>
    /// <param name="size">Edge length in pixels. Pass the shell's small-icon size.</param>
    /// <param name="sessions">
    /// Live sessions. Zero gets the plain mark: a permanent "0" is noise that says
    /// exactly what an undecorated icon already says.
    /// </param>
    public static Icon Create(AgentState state, int size, int sessions = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            DrawMark(graphics, state, size, sessions);
        }

        return FromBitmap(bitmap);
    }

    /// <summary>
    /// Paints the artwork for this state and count, or falls back to a plain disc when
    /// none of it can be loaded.
    /// <para>
    /// The fallback matters more than it looks. A missing or corrupt resource must
    /// still leave a readable status light in the tray, because the icon is the only
    /// thing telling the user whether their machine is reachable — losing the branding
    /// is cosmetic, losing the status is not.
    /// </para>
    /// </summary>
    private static void DrawMark(Graphics graphics, AgentState state, int size, int sessions)
    {
        using Image? mark = LoadMark(state, size, sessions);

        if (mark is null)
        {
            using var fallback = new SolidBrush(ColourFor(state));
            graphics.FillEllipse(fallback, Inset(size));
            return;
        }

        // Full bleed. At 16 pixels every one of them counts, and the artwork is already
        // framed to the tray by scripts/make-icons.ps1 -- insetting here would shrink it
        // twice and cost legibility exactly where it is scarcest.
        var target = new Rectangle(0, 0, size, size);

        graphics.DrawImage(mark, target, 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel);
    }

    private static Color ColourFor(AgentState state) => state switch
    {
        AgentState.Connected => Green,
        AgentState.Reconnecting => Amber,
        _ => Grey,
    };

    private static Rectangle Inset(int size)
    {
        int margin = Math.Max(1, size / 8);

        return new Rectangle(margin, margin, size - (2 * margin) - 1, size - (2 * margin) - 1);
    }

    /// <summary>
    /// Pulls the closest frame out of the embedded <c>.ico</c> for this state and count.
    /// <para>
    /// Every container carries mark-only frames at 16, 20, 24, 32, 40 and 48 — every
    /// size the shell asks for across display scalings — so this is a lookup rather
    /// than a resize, and the glyph stays crisp at 150% where a scaled 16px bitmap
    /// turns to mush.
    /// </para>
    /// <para>
    /// Missing artwork degrades one axis at a time rather than to nothing: the count is
    /// dropped first, then the state. Losing the number is a shame and losing the badge
    /// is worse, but losing the icon that tells the user whether their machine is
    /// reachable is not something a build slip should be able to do.
    /// </para>
    /// </summary>
    private static Image? LoadMark(AgentState state, int size, int sessions)
    {
        return Read(ResourceFor(state, sessions), size)
            ?? Read(ResourceFor(state, 0), size)
            ?? Read(LogoResourceName, size);
    }

    /// <summary>
    /// Which embedded container carries this state and count. The name is assembled
    /// from the same two tokens <c>scripts/make-icons.ps1</c> names the files with.
    /// </summary>
    internal static string ResourceFor(AgentState state, int sessions) => string.Format(
        CultureInfo.InvariantCulture,
        ResourceFormat,
        state switch
        {
            AgentState.Connected => "Connected",
            AgentState.Reconnecting => "Reconnecting",
            _ => "Disconnected",
        },
        sessions switch
        {
            <= 0 => "Base",
            >= CountCeiling => "More",
            _ => sessions.ToString(CultureInfo.InvariantCulture),
        });

    private static Image? Read(string resource, int size)
    {
        try
        {
            using Stream? stream = typeof(TrayArtwork).Assembly
                .GetManifestResourceStream(resource);

            if (stream is null)
            {
                return null;
            }

            using var icon = new Icon(stream, size, size);

            return icon.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a bitmap into a standalone icon.
    /// <para>
    /// Cloned because <see cref="Icon.FromHandle"/> does not take ownership: the icon
    /// would become garbage the moment the handle is destroyed, and leaving the handle
    /// alive instead leaks one per repaint.
    /// </para>
    /// </summary>
    private static Icon FromBitmap(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();

        try
        {
            using Icon borrowed = Icon.FromHandle(handle);

            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
