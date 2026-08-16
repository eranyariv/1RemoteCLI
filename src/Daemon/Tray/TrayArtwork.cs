using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
/// Connected is drawn as the bare mark. Every other state adds a badge, so a decorated
/// tray means something wants attention. Within those states the cues are deliberately
/// redundant: the badge <em>shape</em> survives colour blindness, the badge
/// <em>colour</em> is the fastest to read at a glance, and the treatment of the mark
/// itself — full colour, dimmed, or grey — is the one that still works at 16 pixels on
/// a dark taskbar when the badge is too small to resolve. Any one of the three can fail
/// and the icon still answers the only question it exists to answer: is this working.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayArtwork
{
    /// <summary>Matches the <c>LogicalName</c> the daemon project embeds the icon under.</summary>
    internal const string LogoResourceName = "1RemoteCLI.Tray.Logo.ico";

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
    public static Icon Create(AgentState state, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            DrawMark(graphics, state, size);
            DrawBadge(graphics, state, size);
        }

        return FromBitmap(bitmap);
    }

    /// <summary>
    /// Paints the product mark, or falls back to a plain disc when the artwork cannot
    /// be loaded.
    /// <para>
    /// The fallback matters more than it looks. A missing or corrupt resource must
    /// still leave a readable status light in the tray, because the icon is the only
    /// thing telling the user whether their machine is reachable — losing the branding
    /// is cosmetic, losing the status is not.
    /// </para>
    /// </summary>
    private static void DrawMark(Graphics graphics, AgentState state, int size)
    {
        using Image? mark = LoadMark(size);

        if (mark is null)
        {
            using var fallback = new SolidBrush(ColourFor(state));
            graphics.FillEllipse(fallback, Inset(size));
            return;
        }

        // Full bleed. At 16 pixels every one of them counts, and shrinking the mark to
        // make room for the badge costs legibility exactly where it is scarcest. The
        // badge earns its corner by punching a moat through the mark instead.
        var target = new Rectangle(0, 0, size, size);

        using ImageAttributes attributes = TreatmentFor(state);
        graphics.DrawImage(mark, target, 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>
    /// How the mark is tinted for each state: untouched, dimmed, or drained of colour.
    /// </summary>
    private static ImageAttributes TreatmentFor(AgentState state)
    {
        var attributes = new ImageAttributes();

        ColorMatrix matrix = state switch
        {
            AgentState.Connected => new ColorMatrix(),

            // Dimmed, not greyed: reconnecting is a transient state and should read as
            // "the same thing, faded", not as a different icon. Not dimmed far - below
            // about 0.7 the mark stops resolving against a dark taskbar, and a state
            // you cannot see is worse than one you have to look twice at.
            AgentState.Reconnecting => new ColorMatrix { Matrix33 = 0.7f },

            // Luminance weights, not a flat average: a flat average turns this mark's
            // saturated green into a mid grey that still looks deliberate, where the
            // perceptual weights render it convincingly switched off. Alpha is left
            // alone - signed out is the one state that needs the user to do something,
            // so it is muted by hue and never by fading toward the taskbar.
            _ => new ColorMatrix(
            [
                [0.213f, 0.213f, 0.213f, 0f, 0f],
                [0.715f, 0.715f, 0.715f, 0f, 0f],
                [0.072f, 0.072f, 0.072f, 0f, 0f],
                [0f, 0f, 0f, 1f, 0f],
                [0f, 0f, 0f, 0f, 1f],
            ]),
        };

        attributes.SetColorMatrix(matrix);

        return attributes;
    }

    /// <summary>
    /// Stamps the status badge into the bottom-right corner, inside a punched-out
    /// moat so it never dissolves into the mark behind it.
    /// <para>
    /// Nothing is drawn when the agent is connected. Working is the state the icon is
    /// in almost all the time, and a badge there is noise: a green dot on a green mark
    /// competes with the artwork instead of annotating it, and at 16 pixels the two
    /// merge into one shape. Leaving it clean makes the badge mean something — if the
    /// tray is decorated, something wants attention.
    /// </para>
    /// </summary>
    private static void DrawBadge(Graphics graphics, AgentState state, int size)
    {
        if (state == AgentState.Connected)
        {
            return;
        }

        float diameter = Math.Max(6f, size * 0.46f);
        float x = size - diameter;
        float y = size - diameter;
        var bounds = new RectangleF(x, y, diameter - 1, diameter - 1);

        float moat = Math.Max(1.5f, size * 0.09f);
        var clearing = RectangleF.Inflate(bounds, moat, moat);

        // SourceCopy writes the transparent pixels rather than blending them, which is
        // what actually erases the mark underneath. SourceOver would be a no-op.
        CompositingMode previous = graphics.CompositingMode;
        graphics.CompositingMode = CompositingMode.SourceCopy;

        using (var eraser = new SolidBrush(Color.Transparent))
        {
            graphics.FillEllipse(eraser, clearing);
        }

        graphics.CompositingMode = previous;

        Color colour = ColourFor(state);
        float thickness = Math.Max(1.4f, size * 0.11f);

        using var pen = new Pen(colour, thickness);
        graphics.DrawEllipse(pen, bounds);

        if (state == AgentState.SignedOut)
        {
            graphics.DrawLine(
                pen,
                bounds.Left + (bounds.Width * 0.22f),
                bounds.Bottom - (bounds.Height * 0.22f),
                bounds.Right - (bounds.Width * 0.22f),
                bounds.Top + (bounds.Height * 0.22f));
        }
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
    /// Pulls the closest frame out of the embedded <c>.ico</c>.
    /// <para>
    /// The container carries mark-only frames at 16, 20, 24, 32, 40 and 48 — every
    /// size the shell asks for across display scalings — so this is a lookup rather
    /// than a resize, and the glyph stays crisp at 150% where a scaled 16px bitmap
    /// turns to mush.
    /// </para>
    /// </summary>
    private static Image? LoadMark(int size)
    {
        try
        {
            using Stream? stream = typeof(TrayArtwork).Assembly
                .GetManifestResourceStream(LogoResourceName);

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
