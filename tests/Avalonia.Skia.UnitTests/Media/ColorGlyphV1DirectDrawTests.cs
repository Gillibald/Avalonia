using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Direct <see cref="GlyphRun"/> draws that hold COLR v1-only glyphs, which do not pass the
    /// text layout's colour split: the v1 glyphs draw through their paint graphs, the outline
    /// glyphs through the mask tiers, and none of it through Skia's text blob. The expected
    /// image is the outline glyphs drawn as one run with the v1 glyphs replaced by spaces,
    /// plus each v1 glyph's own drawing at its pen.
    /// </summary>
    public class ColorGlyphV1DirectDrawTests
    {
        private const int Width = 220;
        private const int Height = 140;
        private const double EmSize = 32;
        private const double Advance = 36;

        private static readonly Point s_origin = new(16, 60);

        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 22.5 / 180) *
            Matrix.CreateTranslation(40, 10);

        [Theory]
        [InlineData("H", false)]
        [InlineData("AHB", false)]
        [InlineData("HAH", false)]
        [InlineData("H", true)]
        [InlineData("AHB", true)]
        [InlineData("HAH", true)]
        public void A_Direct_Draw_Renders_V1_Glyphs_Through_Their_Drawings_On_A_Raster_Context(
            string pattern, bool rotated)
        {
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);
            var transform = rotated ? s_rotation : Matrix.CreateTranslation(3, 2);

            AssertDirectDrawMatchesDrawings(typeface, v1Glyph, pattern, transform, Brushes.Black,
                draw => RenderOnRaster(transform, draw));
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl, "H", false)]
        [InlineData(GpuBackend.NativeGl, "AHB", false)]
        [InlineData(GpuBackend.NativeGl, "HAH", false)]
        [InlineData(GpuBackend.NativeGl, "H", true)]
        [InlineData(GpuBackend.NativeGl, "AHB", true)]
        [InlineData(GpuBackend.NativeGl, "HAH", true)]
        [InlineData(GpuBackend.Angle, "H", false)]
        [InlineData(GpuBackend.Angle, "AHB", false)]
        [InlineData(GpuBackend.Angle, "HAH", false)]
        [InlineData(GpuBackend.Angle, "H", true)]
        [InlineData(GpuBackend.Angle, "AHB", true)]
        [InlineData(GpuBackend.Angle, "HAH", true)]
        [InlineData(GpuBackend.Metal, "H", false)]
        [InlineData(GpuBackend.Metal, "AHB", false)]
        [InlineData(GpuBackend.Metal, "HAH", false)]
        [InlineData(GpuBackend.Metal, "H", true)]
        [InlineData(GpuBackend.Metal, "AHB", true)]
        [InlineData(GpuBackend.Metal, "HAH", true)]
        public void A_Direct_Draw_Renders_V1_Glyphs_Through_Their_Drawings_On_A_Gpu_Context(
            GpuBackend backend, string pattern, bool rotated)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            // Compared with the drawings drawn as vectors byte for byte, which the colour masks of
            // the upright tier only approximate on a GPU (they are rasterized on the CPU).
            using var masksOff = ColorGlyphMaskTests.SwitchColorMasksOff();
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);
            var transform = rotated ? s_rotation : Matrix.CreateTranslation(3, 2);

            AssertDirectDrawMatchesDrawings(typeface, v1Glyph, pattern, transform, Brushes.Black,
                draw => RenderOnGpu(gpu!, transform, draw));
        }

        [Fact]
        public void A_Direct_Draw_Matches_The_Text_Layout_Split()
        {
            // The record-time split is the vector path; under managed rasterization it only
            // splits v1 glyphs with colour masks switched off.
            using var masksOff = ColorGlyphMaskTests.SwitchColorMasksOff();
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);
            var transform = Matrix.CreateTranslation(3, 2);

            using var direct = CreateRun(typeface, v1Glyph, "AHB", replaceV1: false);
            using var split = CreateRun(typeface, v1Glyph, "AHB", replaceV1: false);

            var actual = RenderOnRaster(transform, context => context.DrawGlyphRun(Brushes.Black, direct));
            var expected = RenderOnRaster(transform, context =>
                Assert.True(ColorGlyphRunSplitter.TryDraw(context, split, Brushes.Black)));

            // Integral advances put every glyph on the same pen in both routes, so the direct
            // draw and the text layout's split render the same pixels.
            AssertSamePixels(expected, actual);
        }

        [Fact]
        public void A_Direct_Draw_Resolves_The_Foreground_Sentinel_To_The_Brush()
        {
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph, paletteIndex: 0xFFFF);
            var transform = Matrix.CreateTranslation(3, 2);

            var pixels = AssertDirectDrawMatchesDrawings(typeface, v1Glyph, "AHB", transform, Brushes.Green,
                draw => RenderOnRaster(transform, draw), expectRed: false);

            var green = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 1] > 100 && pixels[i] < 60 && pixels[i + 2] < 60)
                {
                    green++;
                }
            }

            Assert.True(green > 8, $"expected green sentinel paint, found {green}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Direct_Draw_Renders_The_V1_Paint_Of_A_Glyph_With_Both_Records(bool rotated)
        {
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV0AndV1Typeface(out var glyph);
            var transform = rotated ? s_rotation : Matrix.CreateTranslation(3, 2);

            // The mask tiers would compose the red v0 layers; the glyph's v1 paint is blue.
            var pixels = AssertDirectDrawMatchesDrawings(typeface, glyph, "AHB", transform, Brushes.Black,
                draw => RenderOnRaster(transform, draw), expectRed: false);

            var (red, blue) = ColorGlyphV1SplitTests.CountRedAndBlue(pixels);

            Assert.True(blue > 8 && red <= 2, $"expected the blue v1 paint, found blue={blue} red={red}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_V1_Only_Run_With_A_Gradient_Never_Reaches_The_Native_Blob(bool rotated)
        {
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);
            var transform = rotated ? s_rotation : Matrix.CreateTranslation(3, 2);

            AssertDirectDrawMatchesDrawings(typeface, v1Glyph, "HH", transform, CreateGradient(),
                draw => RenderOnRaster(transform, draw));
        }

        [Fact]
        public void A_Mixed_Run_With_A_Gradient_Draws_Only_Its_Outline_Stretches_Through_The_Native_Path()
        {
            using var scope = ColorGlyphV1SplitTests.CreateEnvironment();
            var typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out var v1Glyph);
            var transform = Matrix.CreateTranslation(3, 2);

            using var run = CreateRun(typeface, v1Glyph, "AHB", replaceV1: false);
            var impl = (ManagedGlyphRunImpl)run.PlatformImpl.Item;

            var counting = TextTierDiagnostics.CountTiers;

            TextTierDiagnostics.CountTiers = true;
            TextTierDiagnostics.ResetCounters();

            try
            {
                var pixels = RenderOnRaster(transform, context => context.DrawGlyphRun(CreateGradient(), run));

                // Non-solid brushes have no managed tier yet, so each outline stretch takes the
                // native path on its own while the v1 glyph between them draws its paint graph.
                Assert.Equal(2, TextTierDiagnostics.BlobTierDraws);
                Assert.Null(impl.NativeTextArtifact);
                Assert.True(CountRed(pixels) > 8, "the v1 glyph's paint graph did not draw");
            }
            finally
            {
                TextTierDiagnostics.CountTiers = counting;
                TextTierDiagnostics.ResetCounters();
            }
        }

        private static byte[] AssertDirectDrawMatchesDrawings(GlyphTypeface typeface, ushort v1Glyph,
            string pattern, Matrix transform, IBrush brush, Func<Action<DrawingContext>, byte[]> render,
            bool expectRed = true)
        {
            using var run = CreateRun(typeface, v1Glyph, pattern, replaceV1: false);
            using var outlines = CreateRun(typeface, v1Glyph, pattern, replaceV1: true);
            var impl = (ManagedGlyphRunImpl)run.PlatformImpl.Item;

            var counting = TextTierDiagnostics.CountTiers;

            TextTierDiagnostics.CountTiers = true;
            TextTierDiagnostics.ResetCounters();

            byte[] actual;

            try
            {
                actual = render(context => context.DrawGlyphRun(brush, run));

                Assert.Equal(0, TextTierDiagnostics.BlobTierDraws);
                Assert.Null(impl.NativeTextArtifact);
            }
            finally
            {
                TextTierDiagnostics.CountTiers = counting;
                TextTierDiagnostics.ResetCounters();
            }

            var expected = render(context =>
            {
                if (pattern.Replace("H", "").Length > 0)
                {
                    context.DrawGlyphRun(brush, outlines);
                }

                DrawV1Glyphs(context, typeface, v1Glyph, pattern, brush);
            });

            if (expectRed)
            {
                Assert.True(CountRed(actual) > 8, "the v1 glyph's paint graph did not draw");
            }

            AssertSamePixels(expected, actual);

            return actual;
        }

        /// <summary>
        /// Each v1 glyph's own drawing at its pen, in design units scaled to the em size, with
        /// a solid brush's colour as the foreground the palette sentinel resolves to.
        /// </summary>
        private static void DrawV1Glyphs(DrawingContext context, GlyphTypeface typeface, ushort v1Glyph,
            string pattern, IBrush brush)
        {
            var options = brush is ISolidColorBrush solid
                ? new GlyphDrawingOptions
                {
                    Foreground = Color.FromArgb((byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255),
                        solid.Color.R, solid.Color.G, solid.Color.B),
                }
                : null;

            var scale = EmSize / typeface.Metrics.DesignEmHeight;

            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] != 'H')
                {
                    continue;
                }

                var drawing = typeface.GetGlyphDrawing(v1Glyph, options);

                Assert.NotNull(drawing);

                using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                    Matrix.CreateTranslation(s_origin.X + i * Advance, s_origin.Y)))
                {
                    drawing!.Draw(context, default);
                }
            }
        }

        /// <summary>
        /// A run on integral pens; <c>H</c> stands for the v1 glyph, or a space when
        /// <paramref name="replaceV1"/> is set.
        /// </summary>
        private static GlyphRun CreateRun(GlyphTypeface typeface, ushort v1Glyph, string pattern, bool replaceV1)
        {
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < pattern.Length; i++)
            {
                var glyph = pattern[i] == 'H'
                    ? replaceV1 ? typeface.CharacterToGlyphMap[' '] : v1Glyph
                    : typeface.CharacterToGlyphMap[pattern[i]];

                infos.Add(new GlyphInfo(glyph, i, Advance));
            }

            var run = new GlyphRun(typeface, EmSize, default, infos, s_origin);

            Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);

            return run;
        }

        private static IBrush CreateGradient() => new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, Colors.Blue),
                new ImmutableGradientStop(1, Colors.Green),
            });

        private static byte[] RenderOnRaster(Matrix transform, Action<DrawingContext> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                impl.Transform = transform;
                draw(context);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderOnGpu(GpuTestContext gpu, Matrix transform, Action<DrawingContext> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                impl.Transform = transform;
                draw(context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[readInfo.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface!.ReadPixels(readInfo, (IntPtr)p, readInfo.RowBytes, 0, 0));
                }
            }

            return pixels;
        }

        private static int CountRed(byte[] pixels)
        {
            var red = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] > 150 && pixels[i] < 100 && pixels[i + 1] < 100)
                {
                    red++;
                }
            }

            return red;
        }

        private static void AssertSamePixels(byte[] expected, byte[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);

            var differing = 0;
            var first = -1;

            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    differing++;

                    if (first < 0)
                    {
                        first = i;
                    }
                }
            }

            Assert.True(differing == 0,
                $"{differing} bytes differ from the expected image, first at pixel " +
                $"({first / 4 % Width}, {first / 4 / Width})");
        }
    }
}
