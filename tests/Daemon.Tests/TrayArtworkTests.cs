using System.Drawing;
using System.Drawing.Imaging;
using OneRemoteCli.Daemon.Tray;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tray icon is the only thing telling someone whether their machine is
/// reachable, and they read it at 16 pixels out of the corner of an eye. These tests
/// hold that line: the three states must stay tellable apart, and must stay tellable
/// apart <em>without</em> colour.
/// </summary>
public sealed class TrayArtworkTests
{
    private static readonly AgentState[] States =
    [
        AgentState.Connected,
        AgentState.Reconnecting,
        AgentState.SignedOut,
    ];

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void DrawsAtEverySizeTheShellAsksFor(int size)
    {
        foreach (AgentState state in States)
        {
            using Icon icon = TrayArtwork.Create(state, size);
            using Bitmap bitmap = icon.ToBitmap();

            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
            Assert.True(Opaque(bitmap) > 0, $"{state} at {size} drew nothing.");
        }
    }

    [Fact]
    public void CarriesTheProductMarkRatherThanAPlainDisc()
    {
        // The embedded artwork is green on transparent. A fallback disc is a single
        // flat colour, so counting distinct greens separates "the logo loaded" from
        // "the resource was missing and we drew a dot" - which is the thing that
        // would silently undo this change if the resource name ever drifted.
        using Icon icon = TrayArtwork.Create(AgentState.Connected, 32);
        using Bitmap bitmap = icon.ToBitmap();

        var greens = new HashSet<int>();

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);

                if (pixel.A > 32 && pixel.G > pixel.R && pixel.G > pixel.B)
                {
                    greens.Add(pixel.ToArgb());
                }
            }
        }

        Assert.True(greens.Count > 8, $"Expected the antialiased mark, saw {greens.Count} shades.");
    }

    [Fact]
    public void TellsTheThreeStatesApart()
    {
        Dictionary<AgentState, byte[]> pixels = States.ToDictionary(
            state => state,
            state =>
            {
                using Icon icon = TrayArtwork.Create(state, 32);
                using Bitmap bitmap = icon.ToBitmap();

                return Flatten(bitmap);
            });

        foreach (AgentState left in States)
        {
            foreach (AgentState right in States.Where(s => s != left))
            {
                Assert.False(
                    pixels[left].SequenceEqual(pixels[right]),
                    $"{left} and {right} render identically.");
            }
        }
    }

    [Fact]
    public void StaysReadableWithoutColour()
    {
        // Roughly one in twelve men cannot separate red from green, and nobody can
        // separate anything at 16px on a dark taskbar. Stripping the colour out and
        // requiring the shapes still differ is what stops a future change from
        // quietly making colour the only cue.
        Dictionary<AgentState, byte[]> shapes = States.ToDictionary(
            state => state,
            state =>
            {
                using Icon icon = TrayArtwork.Create(state, 32);
                using Bitmap bitmap = icon.ToBitmap();

                return Silhouette(bitmap);
            });

        foreach (AgentState left in States)
        {
            foreach (AgentState right in States.Where(s => s != left))
            {
                int differences = shapes[left]
                    .Zip(shapes[right], (a, b) => a == b ? 0 : 1)
                    .Sum();

                Assert.True(
                    differences > 12,
                    $"{left} and {right} differ in only {differences} pixels once colour is removed.");
            }
        }
    }

    [Fact]
    public void RefusesASizeNothingCouldBeDrawnAt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayArtwork.Create(AgentState.Connected, 4));
    }

    [Fact]
    public void ShowsNothingButTheMarkWhenAllIsWell()
    {
        // Connected is what the tray shows almost all of the time, so it has to be
        // quiet. Comparing it against the raw embedded frame is what stops a future
        // change from decorating the everyday state - at which point a decorated tray
        // would stop meaning "look at me".
        using Stream stream = typeof(TrayArtwork).Assembly
            .GetManifestResourceStream(TrayArtwork.LogoResourceName)
            ?? throw new InvalidOperationException("The tray artwork is not embedded.");

        using var raw = new Icon(stream, 32, 32);
        using Bitmap expected = raw.ToBitmap();

        using Icon icon = TrayArtwork.Create(AgentState.Connected, 32);
        using Bitmap actual = icon.ToBitmap();

        int differences = Silhouette(expected)
            .Zip(Silhouette(actual), (a, b) => a == b ? 0 : 1)
            .Sum();

        Assert.True(differences < 8, $"Connected is not the plain mark ({differences} pixels differ).");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void TellsTheStatesApartOnALightTaskbarAndADarkOne(int sessions)
    {
        // A tray icon is composited straight onto the taskbar, and the taskbar is white
        // on one machine and black on the next. An icon whose state cue only reads
        // against one of them is half broken, and it is the half nobody testing on their
        // own machine will ever see. So each state is flattened onto both extremes and
        // compared against connected by luminance - the channel that survives a theme
        // change, and the one colour blindness cannot take away.
        foreach (Color background in new[] { Color.White, Color.Black })
        {
            using Bitmap reference = Flatten(AgentState.Connected, sessions, background);

            foreach (AgentState state in new[] { AgentState.Reconnecting, AgentState.SignedOut })
            {
                using Bitmap candidate = Flatten(state, sessions, background);

                int differences = 0;

                for (int y = 0; y < reference.Height; y++)
                {
                    for (int x = 0; x < reference.Width; x++)
                    {
                        if (Math.Abs(Luminance(reference.GetPixel(x, y)) - Luminance(candidate.GetPixel(x, y))) > 24)
                        {
                            differences++;
                        }
                    }
                }

                Assert.True(
                    differences > 40,
                    $"{state} looks like connected on a {background.Name} taskbar ({differences} pixels differ).");
            }
        }
    }

    private static Bitmap Flatten(AgentState state, int sessions, Color background)
    {
        using Icon icon = TrayArtwork.Create(state, 16, sessions);
        using Bitmap source = icon.ToBitmap();

        var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(background);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        return flattened;
    }

    private static double Luminance(Color colour) =>
        (0.213 * colour.R) + (0.715 * colour.G) + (0.072 * colour.B);

    [Fact]
    public void ShowsADifferentIconForEveryCount()
    {
        // Ten renderings that have to be ten different pictures. The failure this
        // catches is the quiet one: a resource name that does not resolve falls back
        // to the plain mark, so every count would still draw something and the tray
        // would simply stop counting without anything going red.
        Dictionary<int, byte[]> pixels = Enumerable.Range(0, 10).ToDictionary(
            count => count,
            count =>
            {
                using Icon icon = TrayArtwork.Create(AgentState.Connected, 32, count);
                using Bitmap bitmap = icon.ToBitmap();

                return Flatten(bitmap);
            });

        foreach (int left in pixels.Keys)
        {
            foreach (int right in pixels.Keys.Where(c => c != left))
            {
                Assert.False(
                    pixels[left].SequenceEqual(pixels[right]),
                    $"Counts {left} and {right} render identically.");
            }
        }
    }

    [Fact]
    public void StopsCountingPastTheCeiling()
    {
        // Ten and everything above it are one picture, and it is not the picture for
        // nine: ">9" has to be distinguishable from the last number it replaces, or
        // the tray silently plateaus at nine.
        using Icon nine = TrayArtwork.Create(AgentState.Connected, 32, 9);
        using Bitmap nineBitmap = nine.ToBitmap();

        byte[] expected;

        using (Icon ceiling = TrayArtwork.Create(AgentState.Connected, 32, TrayArtwork.CountCeiling))
        using (Bitmap ceilingBitmap = ceiling.ToBitmap())
        {
            expected = Flatten(ceilingBitmap);
        }

        Assert.False(Flatten(nineBitmap).SequenceEqual(expected), "Nine and the ceiling render identically.");

        foreach (int count in new[] { TrayArtwork.CountCeiling + 1, 47, int.MaxValue })
        {
            using Icon icon = TrayArtwork.Create(AgentState.Connected, 32, count);
            using Bitmap bitmap = icon.ToBitmap();

            Assert.True(Flatten(bitmap).SequenceEqual(expected), $"{count} does not render as the ceiling.");
        }
    }

    [Theory]
    [InlineData(AgentState.Connected)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.SignedOut)]
    public void KeepsCountingWhateverTheConnectionIsDoing(AgentState state)
    {
        // Sessions keep running while the hub is unreachable, so the count means the
        // same thing in every state and has to survive every state's treatment. The
        // greyscale one is the risk: drain the colour and a count drawn in the wrong
        // tone would vanish into the mark.
        using Icon idle = TrayArtwork.Create(state, 16);
        using Icon busy = TrayArtwork.Create(state, 16, 3);
        using Bitmap a = idle.ToBitmap();
        using Bitmap b = busy.ToBitmap();

        int differences = Silhouette(a)
            .Zip(Silhouette(b), (x, y) => x == y ? 0 : 1)
            .Sum();

        Assert.True(differences > 8, $"{state} shows the same shape with and without a count ({differences} pixels).");
    }

    [Theory]
    [InlineData(AgentState.Connected)]
    [InlineData(AgentState.Reconnecting)]
    [InlineData(AgentState.SignedOut)]
    public void ShipsArtworkForEveryStateAndCount(AgentState state)
    {
        // Every state now has its own drawn set rather than a run-time treatment of the
        // connected one, and TrayArtwork falls back quietly when a file is missing. That
        // fallback is the right behaviour in production and a silent failure in a build:
        // a typo in the generator's naming would ship a tray that never leaves the
        // connected icon. Naming the resources from this end is what catches it.
        var counts = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, TrayArtwork.CountCeiling };

        foreach (int count in counts)
        {
            string resource = TrayArtwork.ResourceFor(state, count);

            using Stream? stream = typeof(TrayArtwork).Assembly.GetManifestResourceStream(resource);

            Assert.True(stream is not null, $"{resource} is not embedded.");
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    public void ShipsARealFrameForEverySizeTheShellAsksFor(int size)
    {
        // Every container carries a frame at each of these sizes, so asking for one is
        // a lookup rather than a resize. A missing frame is not an error - GDI+ stretches
        // the nearest one and the count turns to mush - so the only way to notice is to
        // check the frames are there.
        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            foreach (int count in new[] { 0, 1, 5, 9, TrayArtwork.CountCeiling })
            {
                using Stream stream = typeof(TrayArtwork).Assembly
                    .GetManifestResourceStream(TrayArtwork.ResourceFor(state, count))
                    ?? throw new InvalidOperationException($"No artwork is embedded for {state} at a count of {count}.");

                using var icon = new Icon(stream, size, size);

                Assert.Equal(size, icon.Width);
                Assert.Equal(size, icon.Height);
            }
        }
    }

    [Fact]
    public void CarriesTheWordmarkOnlyWhereThereArePixelsToReadIt()
    {
        // The masters are the full lockup, numeral over "CLI", but fitting the wordmark
        // in roughly doubles the height of the artwork and so costs the count digit
        // about half its pixels. The small frames are therefore cropped to the numeral
        // and the count plate, and only 32 and up carry the wordmark.
        //
        // Aspect is what tells them apart without reading the artwork: adding the
        // wordmark under the numeral squares the lockup up, so every large frame lands
        // near 1:1, while a frame cropped to the numeral and the plate is plainly wider
        // than it is tall. Measuring the ink rather than the canvas is the point - both
        // are square bitmaps.
        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            double cropped = InkAspect(state, 5, 24);
            double full = InkAspect(state, 5, 48);

            Assert.True(
                full is > 0.85 and < 1.10,
                $"{state} at 48px should be about square, because the wordmark sits under "
                    + $"the numeral. Got {full:F2}.");

            Assert.True(
                cropped > 1.12,
                $"{state} at 24px should be wider than it is tall, because the wordmark is "
                    + $"cropped away. Got {cropped:F2}.");
        }
    }

    /// <summary>
    /// Width over height of the artwork's opaque extent, ignoring the transparent margin
    /// the square canvas pads it with.
    /// </summary>
    private static double InkAspect(AgentState state, int sessions, int size)
    {
        using Icon icon = TrayArtwork.Create(state, size, sessions);
        using Bitmap bitmap = icon.ToBitmap();

        int left = bitmap.Width;
        int right = -1;
        int top = bitmap.Height;
        int bottom = -1;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 16)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= 0, $"{state} at {size}px drew nothing at all.");

        return (right - left + 1) / (double)(bottom - top + 1);
    }

    private static int Opaque(Bitmap bitmap)
    {
        int count = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 16)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static byte[] Flatten(Bitmap bitmap)
    {
        var bytes = new List<byte>(bitmap.Width * bitmap.Height * 4);

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                bytes.AddRange([pixel.A, pixel.R, pixel.G, pixel.B]);
            }
        }

        return [.. bytes];
    }

    /// <summary>Ink or no ink. Everything colour could have told you, removed.</summary>
    private static byte[] Silhouette(Bitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height];
        int i = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bytes[i++] = bitmap.GetPixel(x, y).A > 96 ? (byte)1 : (byte)0;
            }
        }

        return bytes;
    }
}
