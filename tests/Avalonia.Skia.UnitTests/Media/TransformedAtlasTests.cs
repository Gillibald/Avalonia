using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Static transformed text on GPU contexts draws from the typeface's A8 atlas, one batched
    /// call per run: exactly the pixels of its glyph masks blitted one by one, with no run-sized
    /// bitmap and no copy of the masks in the glyph mask cache.
    /// </summary>
    public class TransformedAtlasTests
    {
        private const string Text = "Wavy AVATAR, fjord; 0123";
        private const int Width = 420;
        private const int Height = 260;

        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 17 / 180) *
            Matrix.CreateTranslation(40.3, 20.6);

        public static IEnumerable<object[]> Contexts()
        {
            yield return new object[] { GpuBackend.NativeGl, false };
            yield return new object[] { GpuBackend.Angle, false };
            yield return new object[] { GpuBackend.NativeGl, true };
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Rotated_Draws_Take_The_Atlas_On_A_Gpu_Context(GpuBackend backend, bool software)
        {
            using var gpu = CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8, 32));

            Draw(gpu, run, s_rotation, Brushes.Black);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.True(typeface.MaskAtlas.Count > 0, "no glyph mask entered the atlas");

            // The atlas is the storage of these masks, not a second copy of cached ones.
            Assert.Equal(0, typeface.MaskCache.Count);

            Assert.True(run.TransformedSprites.TryGet(SpriteKey(run, s_rotation), out var sprites));

            var batches = sprites.Batches;

            Assert.NotNull(batches);

            var drawn = 0;

            foreach (var batch in batches!)
            {
                var arrays = (DrawingContextImpl.SkiaGlyphAtlasBatch)batch.Backend;

                Assert.NotNull(batch.Page);
                Assert.Equal(batch.Count, arrays.Sources.Length);
                Assert.Equal(batch.Count, arrays.Placements.Length);
                drawn += batch.Count;
            }

            Assert.Equal(sprites.Count, drawn);
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Warm_Atlas_Frames_Allocate_Nothing(GpuBackend backend, bool software)
        {
            using var gpu = CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8, 32));

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);
            using var context = CreateContext(gpu, surface);

            context.Transform = s_rotation;

            // Each draw is a frame, which ends with drawing the frame's pending sprites.
            for (var i = 0; i < 2; i++)
            {
                context.DrawGlyphRun(Brushes.Black, run);
                context.FlushGlyphBatch();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 100; i++)
            {
                context.DrawGlyphRun(Brushes.Black, run);
                context.FlushGlyphBatch();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            gpu.GrContext.Flush();

            Assert.True(allocated == 0, $"100 warm atlas draws allocated {allocated} bytes");
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void The_Atlas_Draw_Equals_Its_Glyph_Masks_Blitted_One_By_One(GpuBackend backend, bool software)
        {
            using var gpu = CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Squeezed advances make neighbouring glyphs overlap.
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 22, new Point(8.37, 32.61), advanceScale: 0.8);

            var masks = TransformedGlyphRunTests.GlyphMasksAtPens(typeface, run, s_rotation);

            foreach (var color in s_tints)
            {
                var atlas = Render(gpu, context => context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run));

                // The atlas stores coverage corrected for the colour's luminance bucket: each
                // glyph mask passed through the table and modulated by the plain colour.
                var table = MaskGamma.GetTable(color.R, color.G, color.B);
                var corrected = Render(gpu, context =>
                {
                    var canvas = context.Canvas;

                    canvas.Save();
                    canvas.ResetMatrix();

                    using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B) };

                    foreach (var (mask, x, y) in masks)
                    {
                        if (mask.IsEmpty)
                        {
                            continue;
                        }

                        var alpha = new byte[mask.Width * mask.Height];

                        for (var i = 0; i < alpha.Length; i++)
                        {
                            alpha[i] = table[mask.Alpha[i]];
                        }

                        using var image = CreateAlphaImage(alpha, mask.Width, mask.Height);

                        canvas.DrawImage(image, x + mask.Left, y + mask.Top, new SKSamplingOptions(), paint);
                    }

                    canvas.Restore();
                });

                AssertEqual(corrected, atlas, $"{color} against corrected masks");

                // For an opaque colour these are also the pixels of a single alpha mask, which
                // corrects through a colour filter after modulation.
                var blitted = Render(gpu, context =>
                {
                    // Masks are in device pixels.
                    context.Transform = Matrix.Identity;

                    foreach (var (mask, x, y) in masks)
                    {
                        if (mask.IsEmpty)
                        {
                            continue;
                        }

                        using var image = CreateAlphaImage(mask.Alpha, mask.Width, mask.Height);

                        var source = new Rect(0, 0, mask.Width, mask.Height);

                        ((IAlphaGlyphMaskContext)context).DrawAlphaMask(image, source,
                            source.Translate(new Vector(x + mask.Left, y + mask.Top)), ToArgb(color));
                    }
                });

                AssertEqual(blitted, atlas, $"{color} against filtered masks");
            }
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void The_Atlas_Differs_From_The_Run_Mask_Compose_Only_Where_Glyphs_Overlap(GpuBackend backend, bool software)
        {
            using var gpu = CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 22, new Point(8.37, 32.61), advanceScale: 0.8);

            var masks = TransformedGlyphRunTests.GlyphMasksAtPens(typeface, run, s_rotation);

            // Per pixel: the coverage of every glyph that inks it, in draw order.
            var coverages = new List<byte>?[Width * Height];
            var summed = new byte[Width * Height];

            foreach (var (mask, x, y) in masks)
            {
                for (var row = 0; row < mask.Height; row++)
                {
                    for (var column = 0; column < mask.Width; column++)
                    {
                        var value = mask.Alpha[row * mask.Width + column];

                        if (value != 0)
                        {
                            var index = (y + mask.Top + row) * Width + x + mask.Left + column;

                            (coverages[index] ??= new List<byte>()).Add(value);
                        }
                    }
                }

                RunMaskComposer.ComposeAlpha(mask, x, y, summed, Width, Height);
            }

            var overlapping = 0;

            foreach (var list in coverages)
            {
                if (list is { Count: > 1 })
                {
                    overlapping++;
                }
            }

            Assert.True(overlapping > 20, $"the scene overlaps glyphs at only {overlapping} pixels");

            foreach (var color in s_tints)
            {
                var table = MaskGamma.GetTable(color.R, color.G, color.B);
                var atlas = Render(gpu, context => context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run));

                // A GPU run mask: coverage summed per pixel, corrected once.
                var composed = Render(gpu, context =>
                {
                    context.Transform = Matrix.Identity;

                    using var image = CreateAlphaImage(summed, Width, Height);

                    var bounds = new Rect(0, 0, Width, Height);

                    ((IAlphaGlyphMaskContext)context).DrawAlphaMask(image, bounds, bounds, ToArgb(color));
                });

                var bound = DeriveOverlapBound(table);
                var worst = 0;

                for (var i = 0; i < Width * Height; i++)
                {
                    var difference = Math.Abs(atlas[i * 4 + 3] - composed[i * 4 + 3]);

                    if (coverages[i] is not { Count: > 1 } list)
                    {
                        Assert.True(difference == 0,
                            $"{color}: pixel ({i % Width}, {i / Width}) without overlap differs by {difference}");
                        continue;
                    }

                    // Per glyph, the correction applies before blending: T[a] over T[b]. The
                    // run mask corrected the saturated sum once: T[a + b]. One level more
                    // covers the GPU's rounding of the blend.
                    var over = 0;
                    var sum = 0;

                    foreach (var coverage in list)
                    {
                        var corrected = table[coverage];

                        over = corrected + over - (corrected * over + 127) / 255;
                        sum += coverage;
                    }

                    var allowed = Math.Abs(over - table[Math.Min(255, sum)]) + 1;

                    Assert.True(difference <= allowed,
                        $"{color}: overlap pixel ({i % Width}, {i / Width}) differs by {difference}, allowed {allowed}");

                    worst = Math.Max(worst, difference);
                }

                Assert.True(worst <= bound, $"{color}: worst overlap difference {worst} exceeds the derived bound {bound}");
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{color}: worst overlap difference {worst}, derived bound {bound}");
            }
        }

        /// <summary>
        /// The largest difference two overlapping coverages can produce between correcting each
        /// glyph before blending and correcting their saturated sum, over every pair of
        /// coverages, plus one level of blend rounding.
        /// </summary>
        internal static int DeriveOverlapBound(byte[] table)
        {
            var bound = 0;

            for (var a = 1; a < 256; a++)
            {
                for (var b = 1; b < 256; b++)
                {
                    var over = table[a] + table[b] - (table[a] * table[b] + 127) / 255;

                    bound = Math.Max(bound, Math.Abs(over - table[Math.Min(255, a + b)]));
                }
            }

            return bound + 1;
        }

        private static readonly Color[] s_tints =
        {
            Colors.Black,
            Color.FromArgb(0xFF, 0x20, 0x40, 0x90),
            Color.FromArgb(0xFF, 0xF0, 0xE0, 0x30),
        };

        internal static RunMaskKey SpriteKey(ManagedGlyphRunImpl run, Matrix transform)
        {
            var norm = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));

            Assert.True(GlyphMaskTransform.TryQuantize(transform.M11 / norm, transform.M12 / norm,
                transform.M21 / norm, transform.M22 / norm, out var linear));

            var origin = run.BaselineOrigin;

            GlyphMaskKey.SnapPen((float)(origin.X * transform.M11 + origin.Y * transform.M21 + transform.M31),
                out _, out var phaseX);
            GlyphMaskKey.SnapPen((float)(origin.X * transform.M12 + origin.Y * transform.M22 + transform.M32),
                out _, out var phaseY);

            return new RunMaskKey(GlyphMaskKey.QuantizeScale((float)(run.FontRenderingEmSize * norm)), phaseX,
                GlyphMaskMode.Antialiased, 0u, GridFit: false, PenSnap: false, Transform: linear, OriginPhaseY: phaseY);
        }

        internal static GpuTestContext CreateGpu(GpuBackend backend, bool software)
        {
            var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            // A hardware context registered as software exercises the software GPU route.
            SkiaGpuRasterizer.Register(gpu!.GrContext, software);

            return gpu;
        }

        internal static DrawingContextImpl CreateContext(GpuTestContext gpu, SKSurface? surface)
        {
            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            return new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                GrContext = gpu.GrContext,
                Dpi = new Vector(96, 96),
            });
        }

        private static void Draw(GpuTestContext gpu, ManagedGlyphRunImpl run, Matrix transform, IBrush brush)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var context = CreateContext(gpu, surface))
            {
                context.Transform = transform;
                context.DrawGlyphRun(brush, run);
            }

            gpu.GrContext.Flush();
        }

        /// <summary>Renders on a transparent GPU surface under the test rotation and reads it back as RGBA.</summary>
        internal static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var context = CreateContext(gpu, surface))
            {
                surface!.Canvas.Clear(SKColors.Transparent);
                context.Transform = s_rotation;
                draw(context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }
            }

            return pixels;
        }

        internal static SKImage CreateAlphaImage(byte[] alpha, int width, int height)
            => SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul),
                alpha.AsSpan(0, width * height), width);

        internal static uint ToArgb(Color color)
            => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

        internal static void AssertEqual(byte[] expected, byte[] actual, string label)
        {
            var index = actual.AsSpan().CommonPrefixLength(expected);

            if (index != expected.Length)
            {
                var pixel = index / 4;

                Assert.Fail($"{label}: first difference at ({pixel % Width}, {pixel / Width}) channel {index % 4}: " +
                            $"expected {expected[index]}, actual {actual[index]}");
            }
        }
    }
}
