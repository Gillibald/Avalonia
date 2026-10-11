using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// COLR v1 glyphs drawn under a static rotation, skew or anisotropic scale on hardware GPUs:
    /// each paint graph is rasterized once under the quantized transform into a colour atlas entry
    /// and drawn as a sprite of the transformed tier, instead of replaying its vector fills every
    /// frame. Animated transforms, raster surfaces and the colour atlas switched off keep the
    /// vector replay.
    /// </summary>
    public class TransformedColorAtlasTests
    {
        private const int Width = 640;
        private const int Height = 640;
        private const double EmSize = 32;
        private const double Advance = 40;

        private static readonly Point s_origin = new(60, 96);

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x10));

        public enum Transform
        {
            Rotated17,
            Rotated90,
            Skewed,
            Anisotropic,
        }

        public static IEnumerable<object[]> Contexts()
        {
            foreach (var transform in new[] { Transform.Rotated17, Transform.Skewed })
            {
                yield return new object[] { GpuBackend.NativeGl, true, transform };
                yield return new object[] { GpuBackend.Angle, true, transform };
                yield return new object[] { GpuBackend.Vulkan, true, transform };
            }
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void A_Static_Transformed_V1_Run_Draws_From_Colour_Masks_Rasterized_Once(GpuBackend backend,
            bool updatable, Transform transform)
        {
            using var gpu = ColorAtlasTests.CreateGpu(backend, updatable);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.SegoeUiEmoji, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs);
            var matrix = MatrixOf(transform);

            var first = Measure(() => RenderOnGpu(gpu, matrix, run), out var firstPixels);

            Assert.Equal(0, first.VectorDraws);
            Assert.True(first.Rasterizations > 0, "no colour mask was rasterized");
            Assert.NotEmpty(ColorAtlasTests.ColorAtlasOf(gpu, typeface).GetPages());

            var second = Measure(() => RenderOnGpu(gpu, matrix, run), out var secondPixels);

            Assert.Equal(new Counts(0, 0, 0), second);
            TransformedAtlasTests.AssertEqual(firstPixels, secondPixels, "second frame");
        }

        /// <summary>
        /// A run of colour and outline glyphs under a rotation draws its outline glyphs from the A8
        /// atlas and its colour glyphs from the colour atlas, in run order.
        /// </summary>
        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        [InlineData(GpuBackend.Vulkan)]
        public void A_Transformed_Run_Mixing_Outline_And_Colour_Glyphs_Draws_Both_From_Atlas_Pages(GpuBackend backend)
        {
            using var gpu = ColorAtlasTests.CreateGpu(backend, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs);
            var matrix = MatrixOf(Transform.Rotated17);

            var first = Measure(() => RenderOnGpu(gpu, matrix, run), out var pixels);

            Assert.Equal(0, first.VectorDraws);
            Assert.NotEmpty(TransformedAtlasTests.AtlasOf(gpu, typeface).GetPages());
            Assert.NotEmpty(ColorAtlasTests.ColorAtlasOf(gpu, typeface).GetPages());
            Assert.True(CountColored(pixels) > 20, "the colour glyphs were not drawn");
        }

        public static IEnumerable<object[]> BoundCases()
        {
            foreach (var font in new[]
                     {
                         ColorGlyphMaskTests.Font.V1Solid, ColorGlyphMaskTests.Font.V1Linear,
                         ColorGlyphMaskTests.Font.V1Radial, ColorGlyphMaskTests.Font.SegoeUiEmoji,
                     })
            {
                foreach (var transform in new[] { Transform.Rotated90, Transform.Skewed, Transform.Anisotropic })
                {
                    yield return new object[] { font, transform };
                }
            }
        }

        /// <summary>
        /// The transformed colour masks against the vector replay they replace. The transforms
        /// keep every pen on the masks' quarter-pixel grid and the linear part exactly on its
        /// quantization grid, so both draw the same geometry and only compositing differs, as in
        /// the upright bound: the mask paints the glyph onto transparent and blends the 8-bit
        /// premultiplied result, the vector replay paints each fill onto the destination. The
        /// reference replays the vectors on a raster surface, where the mask's own rasterizer
        /// runs: the GPU draws the mask's bytes 1:1.
        /// </summary>
        /// <remarks>
        /// Measured on Windows x64 with Skia's raster pipeline as the reference and desktop GL
        /// drawing the masks, over every pixel either draw inked on white, all three transforms:
        /// solid fills match exactly, single gradients differ by at most 1 level (mean 0.05-0.10,
        /// higher than upright only because these runs hold no outline glyphs, which match exactly),
        /// Segoe UI Emoji by at most 5 (mean 0.065-0.076). The bounds allow about one and a half
        /// times the largest measured value and twice the mean, as the upright bound does.
        /// </remarks>
        [Theory]
        [MemberData(nameof(BoundCases))]
        public void Transformed_Colour_Masks_Stay_Within_A_Measured_Bound_Of_The_Vector_Replay(
            ColorGlyphMaskTests.Font font, Transform transform)
        {
            using var gpu = ColorAtlasTests.CreateGpu(GpuBackend.NativeGl, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(font, out var glyphs);
            var colorGlyphs = glyphs.Where(g => ColorGlyphRunSplitter.IsV1Glyph(typeface, typeface.ColorTable!, g))
                .ToArray();
            using var run = CreateGlyphRun(typeface, colorGlyphs);
            var matrix = MatrixOf(transform);

            var vectors = RenderOnRaster(matrix, run);
            var masked = Measure(() => RenderOnGpu(gpu, matrix, run), out var maskedPixels);

            Assert.True(masked.Rasterizations > 0, "the run did not draw from colour masks");
            Assert.True(CountColored(vectors) > 20, "the vector replay drew no colour");

            var (max, mean, inked) = Difference(vectors, maskedPixels);
            var maxBound = font == ColorGlyphMaskTests.Font.SegoeUiEmoji ? 8 : 2;
            var meanBound = font == ColorGlyphMaskTests.Font.SegoeUiEmoji ? 0.15 : 0.2;

            Assert.True(inked > 100, $"only {inked} pixels were inked");
            Assert.True(max <= maxBound, $"largest channel difference {max} levels (bound {maxBound}), mean {mean:F3}");
            Assert.True(mean <= meanBound,
                $"mean channel difference {mean:F3} levels over {inked} pixels (bound {meanBound}), max {max}");
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void An_Animating_Rotation_Replays_Vectors_Without_Rasterizing(GpuBackend backend)
        {
            using var gpu = ColorAtlasTests.CreateGpu(backend, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs);

            var frames = Enumerable.Range(0, 8)
                .Select(frame => Measure(() => RenderOnGpu(gpu, Rotation(10 + frame * 3), run), out _))
                .ToList();

            Assert.All(frames.Skip(TransformChurnGuard.Threshold), c =>
            {
                Assert.Equal(0, c.Rasterizations);
                Assert.Equal(2, c.VectorDraws);
            });
        }

        [Fact]
        public void A_Transformed_V1_Run_Replays_Vectors_On_A_Context_Without_Updatable_Textures()
        {
            using var gpu = ColorAtlasTests.CreateGpu(GpuBackend.NativeGl, false);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs);

            var counts = Measure(() => RenderOnGpu(gpu, MatrixOf(Transform.Rotated17), run), out _);

            Assert.Equal(new Counts(0, 2, 0), counts with { Placements = 0 });
        }

        [Fact]
        public void A_Transformed_V1_Run_Replays_Vectors_With_The_Colour_Atlas_Off()
        {
            using var gpu = ColorAtlasTests.CreateGpu(GpuBackend.NativeGl, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs);

            using (ColorAtlasTests.SwitchColorAtlasOff())
            {
                var counts = Measure(() => RenderOnGpu(gpu, MatrixOf(Transform.Rotated17), run), out _);

                Assert.Equal(new Counts(0, 2, 0), counts with { Placements = 0 });
            }
        }

        private readonly record struct Counts(long Rasterizations, long VectorDraws, long Placements);

        private static Counts Measure(Func<byte[]> render, out byte[] pixels)
        {
            var rasterizations = GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread;
            var vectorDraws = GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread;
            var placements = GlyphRasterDiagnostics.AtlasPlacementsOnThread;

            pixels = render();

            return new Counts(GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread - rasterizations,
                GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread - vectorDraws,
                GlyphRasterDiagnostics.AtlasPlacementsOnThread - placements);
        }

        /// <summary>
        /// The test transforms, moving the run's origin to (200, 200), where no transform takes a glyph
        /// past the surface's edge. The exact ones keep pens and the linear part
        /// on their quantization grids: a quarter turn, a horizontal skew of a quarter, and a scale
        /// of 2 by 0.5, whose determinant keeps the em size.
        /// </summary>
        private static Matrix MatrixOf(Transform transform) => transform switch
        {
            Transform.Rotated17 => Rotation(17),
            Transform.Rotated90 => About(new Matrix(0, 1, -1, 0, 0, 0)),
            Transform.Skewed => About(new Matrix(1, 0, -0.25, 1, 0, 0)),
            _ => About(Matrix.CreateScale(2, 0.5)),
        };

        private static Matrix Rotation(double degrees) => About(Matrix.CreateRotation(degrees * Math.PI / 180));

        private static Matrix About(Matrix linear)
            => Matrix.CreateTranslation(-s_origin.X, -s_origin.Y) * linear *
               Matrix.CreateTranslation(200, 200);

        private static GlyphRun CreateGlyphRun(GlyphTypeface typeface, ushort[] glyphs)
        {
            var infos = glyphs.Select((g, i) => new GlyphInfo(g, i, Advance)).ToList();
            var run = new GlyphRun(typeface, EmSize, default, infos, s_origin);

            Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);

            return run;
        }

        private static byte[] RenderOnGpu(GpuTestContext gpu, Matrix transform, GlyphRun run)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                impl.Transform = transform;
                context.DrawGlyphRun(s_text, run);
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

        private static byte[] RenderOnRaster(Matrix transform, GlyphRun run)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                impl.Transform = transform;
                context.DrawGlyphRun(s_text, run);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        // Largest and mean absolute channel difference over the pixels either image inked.
        private static (int Max, double Mean, int Inked) Difference(byte[] expected, byte[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);

            var max = 0;
            long sum = 0;
            var inked = 0;

            for (var i = 0; i < expected.Length; i += 4)
            {
                if (IsWhite(expected, i) && IsWhite(actual, i))
                {
                    continue;
                }

                inked++;

                for (var c = 0; c < 4; c++)
                {
                    var d = Math.Abs(expected[i + c] - actual[i + c]);

                    max = Math.Max(max, d);
                    sum += d;
                }
            }

            return (max, inked == 0 ? 0 : sum / (inked * 4.0), inked);

            static bool IsWhite(byte[] p, int i) => p[i] == 255 && p[i + 1] == 255 && p[i + 2] == 255;
        }

        // Pixels that are neither white, grey nor black: some colour glyph drew there.
        private static int CountColored(byte[] bgra)
        {
            var count = 0;

            for (var i = 0; i < bgra.Length; i += 4)
            {
                if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) -
                    Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2])) > 60)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
