using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Upright grayscale text on a CPU surface that grants direct access blends the run's
    /// untinted coverage straight into the surface, in whatever colour the frame draws it. The
    /// pixels must be those of composing the pre-tinted run mask in that colour and drawing it
    /// with the backend's bitmap blit, whose arithmetic is pinned here.
    /// </summary>
    public class RasterTintChurnTests
    {
        private const int Width = 300;
        private const int Height = 90;

        private static readonly Color[] s_animation =
        {
            Colors.DarkRed, Colors.DarkGreen, Colors.DarkBlue, Colors.DarkOrange, Colors.Purple,
        };

        /// <summary>
        /// The 1:1 blit of a premultiplied BGRA bitmap, per channel with <c>s</c> the source
        /// channel, <c>sa</c> the source alpha and <c>d</c> the destination channel, in the
        /// rounding <paramref name="arithmetic"/> names: the sprite blitter scales the destination
        /// by <c>256 - sa</c> and shifts it down by 8, the 8-bit raster pipeline scales by
        /// <c>255 - sa</c> and divides by 255 as <c>(v + 255) &gt;&gt; 8</c>, and the rounded form
        /// scales by <c>255 - sa</c> and rounds the division to nearest. Every sum saturates.
        /// </summary>
        internal static byte Blit(byte source, byte sourceAlpha, byte destination, GlyphBlitArithmetic arithmetic)
            => (byte)Math.Min(255, source + arithmetic switch
            {
                GlyphBlitArithmetic.Sprite => (destination * (256 - sourceAlpha)) >> 8,
                GlyphBlitArithmetic.Rounded => (destination * (255 - sourceAlpha) + 127) / 255,
                _ => (destination * (255 - sourceAlpha) + 255) >> 8,
            });

        /// <summary>
        /// The rounding the backend's blit uses onto a surface of <paramref name="colorType"/> on
        /// this machine. Skia's ARM64 code divides by 255 with rounding in both its sprite blitter
        /// and its 8-bit raster pipeline (NEON's <c>vrshrq(vrsraq(v, v, 8), 8)</c>, which equals
        /// <c>(v + 127) / 255</c> for every product of two bytes); elsewhere a BGRA bitmap onto a
        /// surface of the platform's BGRA order takes the sprite blitter and anything else the
        /// pipeline.
        /// </summary>
        internal static GlyphBlitArithmetic BackendArithmetic(SKColorType colorType)
            => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? GlyphBlitArithmetic.Rounded
                : colorType == SKColorType.Bgra8888 && SKImageInfo.PlatformColorType == SKColorType.Bgra8888
                    ? GlyphBlitArithmetic.Sprite
                    : GlyphBlitArithmetic.Pipeline;

        [Theory]
        [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul)]
        [InlineData(SKColorType.Bgra8888, SKAlphaType.Opaque)]
        [InlineData(SKColorType.Rgba8888, SKAlphaType.Premul)]
        public unsafe void A_Bitmap_Blit_Blends_Every_Channel_By_The_Formula_Of_Its_Blitter(SKColorType colorType,
            SKAlphaType alphaType)
        {
            var arithmetic = BackendArithmetic(colorType);

            using var scope = WideRunMaskTests.CreateEnvironment(out _);

            // Every source alpha (column) against every destination byte (row); the source
            // channels take the largest and other values a premultiplied pixel allows.
            const int size = 256;
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();

            using var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var framebuffer = bitmap.Lock())
            {
                for (var row = 0; row < size; row++)
                {
                    var pixels = (uint*)((byte*)framebuffer.Address + row * framebuffer.RowBytes);

                    for (var alpha = 0; alpha < size; alpha++)
                    {
                        pixels[alpha] = Source(alpha, row);
                    }
                }
            }

            foreach (var destinationAlpha in alphaType == SKAlphaType.Opaque ? new[] { 255 } : new[] { 255, 200, 64 })
            {
                var info = new SKImageInfo(size, size, colorType, alphaType);

                using var surface = SKSurface.Create(info);

                using (var pixmap = surface.PeekPixels())
                {
                    for (var row = 0; row < size; row++)
                    {
                        var pixels = (uint*)((byte*)pixmap.GetPixels() + row * pixmap.RowBytes);

                        for (var column = 0; column < size; column++)
                        {
                            pixels[column] = Destination(row, destinationAlpha);
                        }
                    }
                }

                using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                       {
                           Surface = surface,
                           Dpi = new Vector(96, 96),
                       }))
                {
                    var rect = new Rect(0, 0, size, size);

                    context.DrawBitmap(bitmap, 1, rect, rect);
                }

                var actual = new uint[size * size];

                fixed (uint* p = actual)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, size * 4, 0, 0));
                }

                for (var row = 0; row < size; row++)
                {
                    var destination = Destination(row, destinationAlpha);

                    for (var alpha = 0; alpha < size; alpha++)
                    {
                        // The bitmap is BGRA; an RGBA surface holds the same channels swapped.
                        var source = Source(alpha, row);

                        if (colorType == SKColorType.Rgba8888)
                        {
                            source = (source & 0xFF00FF00) | ((source >> 16) & 0xFF) | ((source & 0xFF) << 16);
                        }

                        var value = actual[row * size + alpha];

                        for (var shift = 0; shift < 32; shift += 8)
                        {
                            var expected = Blit((byte)(source >> shift), (byte)alpha, (byte)(destination >> shift),
                                arithmetic);

                            Assert.True(expected == (byte)(value >> shift),
                                $"alpha {alpha}, destination {destination:X8}, byte {shift / 8}: expected {expected}, " +
                                $"actual {(byte)(value >> shift)}");
                        }
                    }
                }
            }

            static uint Source(int alpha, int row)
                => ((uint)alpha << 24) | ((uint)(alpha * row / 255) << 16) | ((uint)(alpha / 3) << 8) | (uint)alpha;

            // A premultiplied destination: the row byte where it fits under the alpha.
            static uint Destination(int row, int alpha)
                => ((uint)alpha << 24) | ((uint)Math.Min(row, alpha) << 16) | ((uint)(Math.Min(row, alpha) / 2) << 8) |
                   (uint)(alpha - Math.Min(row, alpha));
        }

        [Fact]
        public void A_Foreground_Colour_Animation_On_A_Raster_Surface_Allocates_Nothing_Per_Frame()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            var brushes = Array.ConvertAll(s_animation, color => (IBrush)new ImmutableSolidColorBrush(color));

            // Two cycles let the animation settle into whatever the steady state holds.
            for (var frame = 0; frame < 2 * brushes.Length; frame++)
            {
                context.DrawGlyphRun(brushes[frame % brushes.Length], run);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var frame = 0; frame < 4 * brushes.Length; frame++)
            {
                context.DrawGlyphRun(brushes[frame % brushes.Length], run);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"{4 * brushes.Length} animated frames allocated {allocated} bytes");
        }

        [Theory]
        [InlineData(SKColorType.Bgra8888)]
        [InlineData(SKColorType.Rgba8888)]
        public void Text_Blended_From_Coverage_Draws_The_Pixels_Of_Its_Pre_Tinted_Mask(SKColorType colorType)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Opaque and translucent tints across the luminance buckets, including mid-grey,
            // whose correction keeps only the contrast shape.
            var tints = new[]
            {
                Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0xCC, 0x20, 0x10),
                Color.FromRgb(0x10, 0xE0, 0x40), Color.FromArgb(0x80, 0x10, 0x60, 0xE0),
                Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFE, 0x20, 0x20, 0x20),
            };

            var backgrounds = new Func<int, int, uint>[]
            {
                static (_, _) => 0,
                static (_, _) => 0xFFFFFFFF,
                static (x, y) => 0xFF000000 | (uint)((x * 7) & 0xFF) << 16 | (uint)((y * 5) & 0xFF) << 8 | (uint)((x ^ y) & 0xFF),
                static (x, y) =>
                {
                    var alpha = (x * 3 + y * 11) & 0xFF;

                    return (uint)alpha << 24 | (uint)(alpha * ((x >> 2) & 0xFF) / 255) << 16 |
                           (uint)(alpha * (y & 0xFF) / 255) << 8 | (uint)(alpha / 2);
                },
            };

            var texts = new (string Text, double Em, Point Origin, double Advance)[]
            {
                // Squeezed advances make neighbouring glyphs overlap.
                ("Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2), 0.8),
                ("The quick brown fox jumps", 11, new Point(3.71, 52.6), 1),
                ("Hamburgefonstiv", 23, new Point(150.12, 80.3), 0.9),
            };

            for (var t = 0; t < tints.Length; t++)
            {
                for (var b = 0; b < backgrounds.Length; b++)
                {
                    var brush = new ImmutableSolidColorBrush(tints[t]);
                    var clipped = (t + b) % 2 == 1;

                    foreach (var (text, em, origin, advance) in texts)
                    {
                        using var blended = WideRunMaskTests.CreateRun(typeface, text, em, origin, advance);
                        using var composed = WideRunMaskTests.CreateRun(typeface, text, em, origin, advance);

                        var expected = Render(colorType, backgrounds[b], clipped, composed, brush, direct: false);
                        var actual = Render(colorType, backgrounds[b], clipped, blended, brush, direct: true);

                        Assert.IsType<RunCoverage>(CachedHandle(blended, RunMaskKey.CoverageTint));
                        Assert.Null(CachedHandle(composed, RunMaskKey.CoverageTint));

                        TransformedAtlasTests.AssertEqual(expected, actual,
                            $"{text} in {tints[t]} on background {b}{(clipped ? ", clipped" : "")}");
                    }
                }
            }
        }

        [Fact]
        public void A_Run_Drawn_In_Many_Colours_On_A_Raster_Surface_Keeps_One_Coverage_And_No_Tinted_Mask()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2));

            foreach (var color in s_animation)
            {
                Render(SKColorType.Bgra8888, static (_, _) => 0xFFFFFFFF, false, run,
                    new ImmutableSolidColorBrush(color), direct: true);
            }

            Assert.Equal(1, run.RunMasks.Count);
            Assert.IsType<RunCoverage>(CachedHandle(run, RunMaskKey.CoverageTint));

            // A surface that grants no direct access still draws the pre-tinted mask.
            Render(SKColorType.Bgra8888, static (_, _) => 0xFFFFFFFF, false, run,
                new ImmutableSolidColorBrush(s_animation[0]), direct: false);

            var tint = RunMaskComposer.MakeTint(255, s_animation[0].R, s_animation[0].G, s_animation[0].B);

            Assert.Equal(2, run.RunMasks.Count);
            Assert.IsAssignableFrom<IBitmapImpl>(CachedHandle(run, tint));
        }

        [Theory]
        [InlineData(nameof(GlyphBlitPath.Scalar), false, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Scalar), false, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Scalar), false, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Scalar), true, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Scalar), true, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Scalar), true, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), false, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), false, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), false, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), true, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), true, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Ssse3), true, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Avx2), false, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Avx2), false, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Avx2), false, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Avx2), true, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Avx2), true, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Avx2), true, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Portable), false, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Portable), false, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Portable), false, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.Portable), true, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.Portable), true, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.Portable), true, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), false, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), false, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), false, nameof(GlyphBlitArithmetic.Rounded))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), true, nameof(GlyphBlitArithmetic.Sprite))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), true, nameof(GlyphBlitArithmetic.Pipeline))]
        [InlineData(nameof(GlyphBlitPath.AdvSimd), true, nameof(GlyphBlitArithmetic.Rounded))]
        public unsafe void Run_Coverage_Blends_By_The_Compose_And_Blit_Arithmetic(string pathName, bool rgba,
            string arithmeticName)
        {
            var arithmetic = Enum.Parse<GlyphBlitArithmetic>(arithmeticName);
            var path = Enum.Parse<GlyphBlitPath>(pathName);

            Assert.SkipUnless(GlyphMaskBlitter.IsSupported(path), $"{path} is not supported on this machine.");

            const int surfaceWidth = 97;
            const int surfaceHeight = 41;

            var random = new Random(4321);

            // Glyph masks of random coverage, with empty and solid stretches, placed so that
            // neighbours overlap, a few of them three deep.
            var masks = new GlyphMask[9];
            var penX = new int[masks.Length];
            var penY = new int[masks.Length];

            for (var i = 0; i < masks.Length; i++)
            {
                var width = 5 + random.Next(30);
                var height = 4 + random.Next(20);
                var alpha = new byte[width * height];

                for (var p = 0; p < alpha.Length; p++)
                {
                    alpha[p] = (p / 7 % 3) switch { 0 => 0, 1 => 255, _ => (byte)random.Next(256) };
                }

                masks[i] = new GlyphMask(alpha, width, height, -random.Next(3), -height + random.Next(4));
                penX[i] = i * 9 + random.Next(4);
                penY[i] = 24 + random.Next(5);
            }

            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale(15), 0, GlyphMaskMode.Antialiased, 0);

            Assert.True(RunCoverage.TryBuild(key, masks, penX, penY, out var coverage));
            Assert.NotNull(coverage);
            Assert.True(coverage!.OverlapPixels.Length > 0, "the masks do not overlap");

            foreach (var tint in new[] { 0xFF000000u, 0xFF2010CCu, 0x80701008u, 0x10101010u, 0xFFFFFFFFu })
            {
                var table = MaskGamma.GetTableForPremulBgra(tint);

                // The pre-tinted run mask, composed by the compose itself.
                var mask = new byte[coverage.Width * coverage.Height * 4];

                for (var i = 0; i < masks.Length; i++)
                {
                    RunMaskComposer.ComposeTinted(masks[i], penX[i] - coverage.OffsetX, penY[i] - coverage.OffsetY, tint,
                        mask, coverage.Width, coverage.Height, 0, table);
                }

                var surface = new uint[surfaceWidth * surfaceHeight];

                for (var i = 0; i < surface.Length; i++)
                {
                    var alpha = i % 3 == 0 ? 255 : random.Next(256);
                    var pixel = (uint)alpha << 24;

                    for (var shift = 0; shift < 24; shift += 8)
                    {
                        pixel |= (uint)random.Next(alpha + 1) << shift;
                    }

                    surface[i] = pixel;
                }

                var expected = (uint[])surface.Clone();
                var x = -2 + coverage.OffsetX;
                var y = -9 + coverage.OffsetY;

                // The clip cuts the coverage on every side.
                var clip = new PixelRect(3, 1, 80, 30);

                for (var row = 0; row < coverage.Height; row++)
                {
                    for (var column = 0; column < coverage.Width; column++)
                    {
                        var sx = x + column;
                        var sy = y + row;

                        if (sx < clip.X || sx >= clip.Right || sy < clip.Y || sy >= clip.Bottom)
                        {
                            continue;
                        }

                        var source = BitConverter.ToUInt32(mask, (row * coverage.Width + column) * 4);

                        if (rgba)
                        {
                            source = (source & 0xFF00FF00) | ((source >> 16) & 0xFF) | ((source & 0xFF) << 16);
                        }

                        ref var d = ref expected[sy * surfaceWidth + sx];
                        var result = 0u;

                        for (var shift = 0; shift < 32; shift += 8)
                        {
                            result |= (uint)Blit((byte)(source >> shift), (byte)(source >> 24), (byte)(d >> shift),
                                arithmetic) << shift;
                        }

                        d = result;
                    }
                }

                var previous = GlyphMaskBlitter.Path;

                try
                {
                    GlyphMaskBlitter.Path = path;

                    fixed (uint* pixels = surface)
                    {
                        var target = new GlyphBlitTarget((IntPtr)pixels, surfaceWidth * 4, surfaceWidth, surfaceHeight,
                            clip, rgba, arithmetic);

                        GlyphMaskBlitter.BlendRunCoverage(target, coverage, x, y, tint, table);
                    }
                }
                finally
                {
                    GlyphMaskBlitter.Path = previous;
                }

                for (var i = 0; i < surface.Length; i++)
                {
                    Assert.True(expected[i] == surface[i],
                        $"tint {tint:X8}, pixel ({i % surfaceWidth}, {i / surfaceWidth}): expected {expected[i]:X8}, " +
                        $"actual {surface[i]:X8}");
                }
            }
        }

        /// <summary>
        /// The handle of the run's cached mask in <paramref name="tint"/> at the scale, phase and
        /// hinting the test runs are drawn with, or <c>null</c>.
        /// </summary>
        private static IDisposable? CachedHandle(ManagedGlyphRunImpl run, uint tint)
        {
            GlyphMaskKey.SnapPen((float)run.BaselineOrigin.X, out _, out var phase);

            var gasp = run.GlyphTypeface.Gasp;
            var em = run.FontRenderingEmSize;
            var gridFit = !gasp.IsBelowHintingFloor(em);
            var penSnap = gridFit && (gasp.WantsFullGridFit(em) ||
                                      (gasp.WantsBytecodeGridFit(em) && run.GlyphTypeface.HasTrueTypeHinting));

            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)em), penSnap ? (byte)0 : phase,
                GlyphMaskMode.Antialiased, tint, gridFit, penSnap);

            return run.RunMasks.TryGet(key, out var mask) ? mask.Parts[0].Handle : null;
        }

        private static unsafe byte[] Render(SKColorType colorType, Func<int, int, uint> background, bool clipped,
            ManagedGlyphRunImpl run, IBrush brush, bool direct)
        {
            var info = new SKImageInfo(Width, Height, colorType, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            using (var pixmap = surface.PeekPixels())
            {
                for (var row = 0; row < Height; row++)
                {
                    var pixels = (uint*)((byte*)pixmap.GetPixels() + row * pixmap.RowBytes);

                    for (var column = 0; column < Width; column++)
                    {
                        pixels[column] = background(column, row);
                    }
                }
            }

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                context.AllowsDirectSurfaceWrites = direct;

                if (clipped)
                {
                    context.PushClip(new Rect(17, 21, 190, 50));
                }

                context.DrawGlyphRun(brush, run);

                if (clipped)
                {
                    context.PopClip();
                }
            }

            var result = new byte[info.BytesSize];

            fixed (byte* p = result)
            {
                Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
            }

            return result;
        }
    }
}
