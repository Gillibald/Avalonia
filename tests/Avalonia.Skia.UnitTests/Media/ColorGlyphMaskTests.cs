using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// COLR v1 glyphs under managed rasterization drawn from colour masks: each paint graph is
    /// rasterized once per scale bucket and phase into premultiplied BGRA, cached per typeface and
    /// charged to the glyph cache budget, and upright runs compose their run masks from it instead
    /// of replaying the glyph's vector fills on every draw. Draws the upright tier does not take
    /// keep the vector replay.
    /// </summary>
    public class ColorGlyphMaskTests
    {
        private const int Width = 240;
        private const int Height = 120;
        private const double EmSize = 32;
        private const double Advance = 40;

        private static readonly Point s_origin = new(12, 64);

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x10));

        public enum Font
        {
            V1Solid,
            V1Linear,
            V1Radial,
            V1Sweep,
            V0AndV1,
            SegoeUiEmoji,
        }

        public enum Declined
        {
            Rotated,
            Oversized,
            TransparentForeground,
        }

        [Theory]
        [InlineData(Font.V1Solid)]
        [InlineData(Font.V1Radial)]
        [InlineData(Font.V0AndV1)]
        [InlineData(Font.SegoeUiEmoji)]
        public void A_V1_Run_Is_Drawn_From_Colour_Masks_Rasterized_Once(Font font)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(font, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs, s_text);

            var first = Measure(() => RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(s_text, run)),
                out var firstPixels);

            Assert.Equal(DistinctColorGlyphs(glyphs, typeface), first.Rasterizations);
            Assert.Equal(0, first.VectorDraws);
            Assert.True(CountColored(firstPixels) > 20, "the run drew no colour");

            var second = Measure(() => RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(s_text, run)),
                out var secondPixels);

            Assert.Equal(0, second.Rasterizations);
            Assert.Equal(0, second.VectorDraws);
            AssertSamePixels(firstPixels, secondPixels);
        }

        [Fact]
        public void Text_Layout_Keeps_V1_Glyphs_In_The_Run_And_Its_Render_Data_Draws_From_Colour_Masks()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.V1Linear, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs, s_text);
            using var recording = new RenderDataDrawingContext(null);

            Assert.False(ColorGlyphRunSplitter.TryDraw(recording, run, s_text));
            recording.DrawGlyphRun(s_text, run);

            var stream = recording.GetRenderStream();

            try
            {
                var first = Measure(() => RenderOnRaster(Matrix.Identity, (_, impl) => stream!.Replay(impl)),
                    out var pixels);
                var second = Measure(() => RenderOnRaster(Matrix.Identity, (_, impl) => stream!.Replay(impl)), out _);

                Assert.Equal(1, first.Rasterizations);
                Assert.Equal(0, first.VectorDraws);
                Assert.Equal(0, second.Rasterizations);
                Assert.Equal(0, second.VectorDraws);
                Assert.True(CountColored(pixels) > 20, "the render data drew no colour");
            }
            finally
            {
                stream?.DisposeResources();
                stream?.Dispose();
            }
        }

        [Fact]
        public void Scrolling_By_Whole_Pixels_Reuses_The_Colour_Masks_And_A_New_Phase_Rasterizes_Once()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.SegoeUiEmoji, out var glyphs);
            var distinct = DistinctColorGlyphs(glyphs, typeface);

            Assert.Equal(distinct, DrawAt(new Vector(0, 0)).Rasterizations);

            foreach (var offset in new[] { new Vector(0, 7), new Vector(5, 0), new Vector(-3, 11) })
            {
                var scrolled = DrawAt(offset);

                Assert.Equal(0, scrolled.Rasterizations);
                Assert.Equal(0, scrolled.VectorDraws);
            }

            Assert.Equal(distinct, DrawAt(new Vector(0.25, 0)).Rasterizations);
            Assert.Equal(0, DrawAt(new Vector(9.25, -4)).Rasterizations);

            Counts DrawAt(Vector offset)
            {
                using var run = CreateGlyphRun(typeface, glyphs, s_text, s_origin + offset);

                return Measure(() => RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(s_text, run)),
                    out _);
            }
        }

        [Fact]
        public void Colour_Masks_Are_Charged_To_The_Budget_And_Credited_When_Evicted()
        {
            using var scope = CreateEnvironment();
            var budget = new GlyphCacheBudget(64L * 1024 * 1024);
            var typeface = CreateTypeface(Font.V1Radial, out var glyphs);

            typeface.CacheBudget = budget;

            DrawOnce(typeface, glyphs);

            var colorMasks = typeface.ColorMaskCache;
            var cost = colorMasks.TotalCost;

            Assert.Equal(1, colorMasks.Count);
            Assert.True(cost > 48, $"the colour mask charged {cost} bytes");
            Assert.Equal(typeface.MaskCache.TotalCost + cost, budget.BytesOf(GlyphCachePoolKind.Masks));

            var freed = ((IGlyphCachePool)colorMasks).EvictOldest(long.MaxValue, long.MaxValue);

            Assert.Equal(cost, freed);
            Assert.Equal(0, colorMasks.Count);
            Assert.Equal(typeface.MaskCache.TotalCost, budget.BytesOf(GlyphCachePoolKind.Masks));

            // A run composed before the eviction keeps its own copy; a new run rasterizes again.
            Assert.Equal(1, DrawOnce(typeface, glyphs).Rasterizations);
            Assert.Equal(cost, colorMasks.TotalCost);
        }

        [Fact]
        public void Disposing_The_Typeface_Credits_Its_Colour_Masks()
        {
            using var scope = CreateEnvironment();
            var budget = new GlyphCacheBudget(64L * 1024 * 1024);
            var typeface = CreateTypeface(Font.V1Radial, out var glyphs);

            typeface.CacheBudget = budget;

            DrawOnce(typeface, glyphs);

            Assert.True(typeface.ColorMaskCache.TotalCost > 0);

            typeface.Dispose();

            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Masks));
        }

        [Fact]
        public void Sentinel_Paints_Key_Colour_Masks_By_Foreground_And_Other_Paints_Share_Them()
        {
            using var scope = CreateEnvironment();
            var sentinel = CreateV1Typeface(BuildColrV1Solid, out var sentinelGlyph, paletteEntry: 0xFFFF);
            var plain = CreateTypeface(Font.V1Solid, out var plainGlyphs);
            var sentinelGlyphs = new[] { plainGlyphs[0], sentinelGlyph, plainGlyphs[2], sentinelGlyph };
            var red = new ImmutableSolidColorBrush(Colors.Red);
            var green = new ImmutableSolidColorBrush(Colors.Green);

            Assert.Equal(1, DrawOnce(sentinel, sentinelGlyphs, red).Rasterizations);
            Assert.Equal(1, DrawOnce(sentinel, sentinelGlyphs, green).Rasterizations);
            Assert.Equal(0, DrawOnce(sentinel, sentinelGlyphs, red).Rasterizations);

            Assert.Equal(1, DrawOnce(plain, plainGlyphs, red).Rasterizations);
            Assert.Equal(0, DrawOnce(plain, plainGlyphs, green).Rasterizations);

            // The sentinel follows the brush: the green draw inks green, not the red of the first.
            using var run = CreateGlyphRun(sentinel, sentinelGlyphs, green);
            var pixels = RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(green, run));

            Assert.True(CountPixels(pixels, (r, g, b) => g > 100 && r < 40 && b < 40) > 20,
                "the sentinel glyph did not take the green brush");
        }

        [Theory]
        [InlineData(Declined.Rotated)]
        [InlineData(Declined.Oversized)]
        [InlineData(Declined.TransparentForeground)]
        public void Draws_The_Upright_Tier_Declines_Replay_Vectors_Without_Colour_Masks(Declined declined)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.V1Solid, out var glyphs);
            var brush = declined == Declined.TransparentForeground
                ? new ImmutableSolidColorBrush(Colors.Transparent)
                : s_text;
            var transform = declined switch
            {
                Declined.Rotated => Matrix.CreateRotation(Math.PI * 17 / 180) * Matrix.CreateTranslation(30, -20),
                Declined.Oversized => Matrix.CreateScale(6, 6) * Matrix.CreateTranslation(-60, -340),
                _ => Matrix.Identity,
            };

            using var run = CreateGlyphRun(typeface, glyphs, brush);

            var counts = Measure(() => RenderOnRaster(transform, (context, _) => context.DrawGlyphRun(brush, run)),
                out var pixels);

            Assert.Equal(0, counts.Rasterizations);
            Assert.Equal(2, counts.VectorDraws);
            Assert.True(CountColored(pixels) > 20, "the colour glyphs were not drawn");
        }

        [Theory]
        [InlineData(null)]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void An_Animating_Upright_Scale_Stretches_The_Settled_Mask_Instead_Of_Rasterizing(GpuBackend? backend)
        {
            using var gpu = backend is { } b ? GpuTestContext.TryCreate(b, out _) : null;

            Assert.SkipWhen(backend is not null && gpu is null, $"No usable {backend} context");

            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs, s_text);

            Counts DrawAt(double scale)
            {
                var transform = Matrix.CreateScale(scale, scale);

                return gpu is null
                    ? Measure(() => RenderOnRaster(transform, (context, _) => context.DrawGlyphRun(s_text, run)), out _)
                    : Measure(() => RenderOnGpu(gpu, transform, (context, _) => context.DrawGlyphRun(s_text, run)),
                        out _);
            }

            // The first frames rasterize at their scales until the run counts as animating
            // (TransformChurnGuard.Threshold changes); from then on the settled mask stretches,
            // since the zoom stays within the stretch band.
            var frames = Enumerable.Range(0, 12).Select(frame => DrawAt(1 + frame * 0.01)).ToList();

            Assert.All(frames.Take(TransformChurnGuard.Threshold), c => Assert.True(c.Rasterizations > 0));
            Assert.All(frames.Skip(TransformChurnGuard.Threshold), c => Assert.Equal(new Counts(0, 0), c));

            // Once the scale holds, the run rasterizes at it and draws from masks again.
            var settled = DrawAt(1.11);

            Assert.True(settled.Rasterizations > 0);
            Assert.Equal(0, settled.VectorDraws);
            Assert.Equal(new Counts(0, 0), DrawAt(1.11));
        }

        [Fact]
        public void An_Animating_Upright_Scale_Beyond_The_Stretch_Band_Replays_Vectors()
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.V1Radial, out var glyphs);
            using var run = CreateGlyphRun(typeface, glyphs, s_text);

            var frames = Enumerable.Range(0, 8).Select(frame => Measure(() => RenderOnRaster(
                Matrix.CreateScale(1 + frame * 0.5, 1 + frame * 0.5),
                (context, _) => context.DrawGlyphRun(s_text, run)), out _)).ToList();

            Assert.All(frames.Skip(TransformChurnGuard.Threshold), c => Assert.Equal(new Counts(0, 2), c));
        }

        public static IEnumerable<object[]> BoundCases()
        {
            foreach (Font font in Enum.GetValues(typeof(Font)))
            {
                for (var phase = 0; phase < GlyphMaskKey.PhaseCount; phase++)
                {
                    yield return new object[] { font, phase };
                }
            }
        }

        /// <summary>
        /// The colour masks against the vector replay they replace, on a raster surface with the
        /// pens on the masks' quarter-pixel grid and the baseline on a whole pixel, so both draw
        /// the same geometry and only compositing differs: the mask paints the glyph onto
        /// transparent and blends the 8-bit premultiplied result, the vector replay paints each
        /// fill onto the destination.
        /// </summary>
        /// <remarks>
        /// Measured on Windows x64 with Skia's raster pipeline over every pixel either draw inked
        /// on the white background, all four phases: solid fills match exactly and single
        /// gradients differ by at most 1 level (mean 0.03-0.05), where the gradient's 8-bit colour
        /// is rounded once more by the source-over blend of the mask; Segoe UI Emoji, whose glyphs
        /// stack up to a hundred translucent gradient fills that each round onto the layer below,
        /// differs by at most 5 levels (mean 0.058-0.065). The bounds allow about one and a half
        /// times the largest measured value and twice the mean, so a misplaced mask (a phase or
        /// pad error moves whole edges, tens of levels at hundreds of pixels) or a lost composite
        /// fails at once.
        /// </remarks>
        [Theory]
        [MemberData(nameof(BoundCases))]
        public void Colour_Masks_Stay_Within_A_Measured_Bound_Of_The_Vector_Replay(Font font, int phase)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(font, out var glyphs);
            var origin = s_origin + new Vector(phase / (double)GlyphMaskKey.PhaseCount, 0);

            byte[] Draw(bool masks)
            {
                var previous = ColorGlyphRunSplitter.UseColorMasks;
                ColorGlyphRunSplitter.UseColorMasks = masks;

                try
                {
                    using var run = CreateGlyphRun(typeface, glyphs, s_text, origin);

                    return RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(s_text, run));
                }
                finally
                {
                    ColorGlyphRunSplitter.UseColorMasks = previous;
                }
            }

            var vectors = Draw(masks: false);
            var masked = Measure(() => Draw(masks: true), out var maskedPixels);

            Assert.True(masked.Rasterizations > 0, "the run did not draw from colour masks");
            Assert.True(CountColored(vectors) > 20, "the vector replay drew no colour");

            var (max, mean, inked) = Difference(vectors, maskedPixels);
            var maxBound = font == Font.SegoeUiEmoji ? 8 : 2;
            var meanBound = font == Font.SegoeUiEmoji ? 0.15 : 0.1;

            Assert.True(inked > 100, $"only {inked} pixels were inked");
            Assert.True(max <= maxBound, $"largest channel difference {max} levels (bound {maxBound}), mean {mean:F3}");
            Assert.True(mean <= meanBound,
                $"mean channel difference {mean:F3} levels over {inked} pixels (bound {meanBound}), max {max}");
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void Colour_Masks_Draw_The_Same_Pixels_On_Raster_And_Gpu_Contexts(GpuBackend backend)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Font.V1Radial, out var glyphs);

            using var rasterRun = CreateGlyphRun(typeface, glyphs, s_text);
            var raster = RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(s_text, rasterRun));

            using var gpuRun = CreateGlyphRun(typeface, glyphs, s_text);
            var onGpu = Measure(() => RenderOnGpu(gpu!, Matrix.Identity,
                (context, _) => context.DrawGlyphRun(s_text, gpuRun)), out var gpuPixels);

            Assert.Equal(0, onGpu.VectorDraws);

            var (max, _, _) = Difference(raster, gpuPixels);

            // Both draw the same mask bytes 1:1 at a whole-pixel offset; measured identical on
            // desktop GL and ANGLE, a level is left for a GPU whose blend rounds differently.
            Assert.True(max <= 1, $"largest channel difference {max} levels between raster and GPU");
        }

        /// <summary>
        /// Draws v1 glyphs as vectors until disposed, for tests of the vector path that the
        /// upright tier's colour masks would otherwise take.
        /// </summary>
        internal static IDisposable SwitchColorMasksOff() => new ColorMaskSwitch();

        private sealed class ColorMaskSwitch : IDisposable
        {
            private readonly bool _previous = ColorGlyphRunSplitter.UseColorMasks;

            public ColorMaskSwitch() => ColorGlyphRunSplitter.UseColorMasks = false;

            public void Dispose() => ColorGlyphRunSplitter.UseColorMasks = _previous;
        }

        private static Counts DrawOnce(GlyphTypeface typeface, ushort[] glyphs, IBrush? brush = null)
        {
            brush ??= s_text;

            using var run = CreateGlyphRun(typeface, glyphs, brush);

            return Measure(() => RenderOnRaster(Matrix.Identity, (context, _) => context.DrawGlyphRun(brush, run)),
                out _);
        }

        private readonly record struct Counts(long Rasterizations, long VectorDraws);

        private static Counts Measure(Func<byte[]> render, out byte[] pixels)
        {
            var rasterizations = GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread;
            var vectorDraws = GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread;

            pixels = render();

            return new Counts(GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread - rasterizations,
                GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread - vectorDraws);
        }

        private static int DistinctColorGlyphs(ushort[] glyphs, GlyphTypeface typeface)
            => glyphs.Distinct().Count(g => ColorGlyphRunSplitter.IsV1Glyph(typeface, typeface.ColorTable!, g));

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

        private static GlyphRun CreateGlyphRun(GlyphTypeface typeface, ushort[] glyphs, IBrush brush,
            Point? origin = null)
        {
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < glyphs.Length; i++)
            {
                infos.Add(new GlyphInfo(glyphs[i], i, Advance));
            }

            var run = new GlyphRun(typeface, EmSize, default, infos, origin ?? s_origin);

            Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);

            return run;
        }

        /// <summary>
        /// The typeface for <paramref name="font"/> and a run of outline glyph, colour glyph,
        /// outline glyph, colour glyph (Segoe UI Emoji: four different emoji).
        /// </summary>
        private static GlyphTypeface CreateTypeface(Font font, out ushort[] glyphs)
        {
            GlyphTypeface typeface;
            ushort color;

            switch (font)
            {
                case Font.V1Solid:
                    typeface = CreateV1Typeface(BuildColrV1Solid, out color);
                    break;
                case Font.V1Linear:
                    typeface = CreateV1Typeface(BuildColrV1LinearGradient, out color);
                    break;
                case Font.V1Radial:
                    typeface = CreateV1Typeface(BuildColrV1RadialGradient, out color);
                    break;
                case Font.V1Sweep:
                    typeface = CreateV1Typeface(BuildColrV1SweepGradient, out color);
                    break;
                case Font.V0AndV1:
                    typeface = ColorGlyphV1SplitTests.CreateV0AndV1Typeface(out color);
                    break;
                default:
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                        "seguiemj.ttf");

                    Assert.SkipUnless(File.Exists(path), "Segoe UI Emoji is not installed.");

                    typeface = TestGlyphTypefaces.FromSKTypeface(SKTypeface.FromFile(path));

                    var map = typeface.CharacterToGlyphMap;

                    // A face (gradients), a fox (layers and transforms), a rainbow and a party popper.
                    glyphs = new[] { map[0x1F600], map[0x1F98A], map[0x1F308], map[0x1F389] };
                    Assert.DoesNotContain((ushort)0, glyphs);

                    return typeface;
                }
            }

            glyphs = new[] { typeface.CharacterToGlyphMap['A'], color, typeface.CharacterToGlyphMap['B'], color };

            return typeface;
        }

        private delegate byte[] ColrBuilder(ushort baseGlyph, ushort outlineGlyph, Rect ink, ushort paletteEntry);

        /// <summary>
        /// Inter with a COLR v1 record for 'H' that paints the 'A' outline with the paint
        /// <paramref name="build"/> writes, over a red and blue palette.
        /// </summary>
        private static GlyphTypeface CreateV1Typeface(ColrBuilder build, out ushort v1Glyph, ushort paletteEntry = 0)
        {
            var baseFont = SyntheticFont.FromBytes(LoadFontBytes("Inter-Regular.ttf"));
            var probe = baseFont.TryCreateGlyphTypeface();

            Assert.NotNull(probe);

            var baseGlyph = probe!.CharacterToGlyphMap['H'];
            var outlineGlyph = probe.CharacterToGlyphMap['A'];
            var ink = probe.GetGlyphOutline(outlineGlyph)!.Bounds;

            var grafted = ColrTestFont.Graft(baseFont, build(baseGlyph, outlineGlyph, ink, paletteEntry),
                ColrTestFont.Cpal(new[] { Colors.Red, Colors.Blue })).ToBytes();

            using var skData = SKData.CreateCopy(grafted);
            var skTypeface = SKTypeface.FromData(skData);

            Assert.NotNull(skTypeface);

            v1Glyph = baseGlyph;

            return TestGlyphTypefaces.FromSKTypeface(skTypeface!);
        }

        private static byte[] BuildColrV1Solid(ushort baseGlyph, ushort outlineGlyph, Rect ink, ushort paletteEntry)
        {
            var colr = BeginColrV1PaintGlyph(baseGlyph, outlineGlyph);

            colr.UInt8(2);
            colr.UInt16(paletteEntry);
            colr.F2Dot14(1.0);

            return colr.ToArray();
        }

        private static byte[] BuildColrV1LinearGradient(ushort baseGlyph, ushort outlineGlyph, Rect ink,
            ushort paletteEntry)
        {
            var colr = BeginColrV1PaintGlyph(baseGlyph, outlineGlyph);

            colr.UInt8(4);
            colr.UInt24(16);
            colr.Int16((short)ink.X).Int16(0).Int16((short)ink.Right).Int16(0).Int16((short)ink.X).Int16(1000);
            WriteRedToBlueColorLine(colr);

            return colr.ToArray();
        }

        private static byte[] BuildColrV1RadialGradient(ushort baseGlyph, ushort outlineGlyph, Rect ink,
            ushort paletteEntry)
        {
            var colr = BeginColrV1PaintGlyph(baseGlyph, outlineGlyph);
            var center = ink.Center;

            colr.UInt8(6);
            colr.UInt24(16);
            colr.Int16((short)center.X).Int16((short)center.Y).UInt16(0);
            colr.Int16((short)center.X).Int16((short)center.Y).UInt16((ushort)(Math.Max(ink.Width, ink.Height) / 2));
            WriteRedToBlueColorLine(colr);

            return colr.ToArray();
        }

        private static byte[] BuildColrV1SweepGradient(ushort baseGlyph, ushort outlineGlyph, Rect ink,
            ushort paletteEntry)
        {
            var colr = BeginColrV1PaintGlyph(baseGlyph, outlineGlyph);
            var center = ink.Center;

            colr.UInt8(8);
            colr.UInt24(12);
            colr.Int16((short)center.X).Int16((short)center.Y);
            colr.Int16(0).Int16(16384);
            WriteRedToBlueColorLine(colr);

            return colr.ToArray();
        }

        // The COLR v1 header and base glyph list up to the PaintGlyph that wraps the fill paint the
        // caller writes next; the layout is sequential, so the PaintGlyph's sub-paint offset is 6.
        private static BigEndianBuffer BeginColrV1PaintGlyph(ushort baseGlyph, ushort outlineGlyph)
        {
            var colr = new BigEndianBuffer();

            colr.UInt16(1);
            colr.UInt16(0);
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt16(0);
            var baseListOffsetPos = colr.ReserveOffset32();
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt32(0);
            colr.UInt32(0);

            var baseListStart = colr.Position;
            colr.PatchUInt32(baseListOffsetPos, (uint)baseListStart);
            colr.UInt32(1);
            colr.UInt16(baseGlyph);
            var recordPaintOffsetPos = colr.ReserveOffset32();

            colr.PatchUInt32(recordPaintOffsetPos, (uint)(colr.Position - baseListStart));
            colr.UInt8(10);
            colr.UInt24(6);
            colr.UInt16(outlineGlyph);

            return colr;
        }

        // Pad extend, palette entry 0 at the start and 1 at the end.
        private static void WriteRedToBlueColorLine(BigEndianBuffer colr)
        {
            colr.UInt8(0);
            colr.UInt16(2);
            colr.F2Dot14(0.0).UInt16(0).F2Dot14(1.0);
            colr.F2Dot14(1.0).UInt16(1).F2Dot14(1.0);
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            return scope;
        }

        private static byte[] RenderOnRaster(Matrix transform, Action<DrawingContext, IDrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                impl.Transform = transform;
                draw(context, impl);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderOnGpu(GpuTestContext gpu, Matrix transform,
            Action<DrawingContext, IDrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                impl.Transform = transform;
                draw(context, impl);
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

        // Pixels that are neither white, grey nor black: some colour glyph drew there.
        private static int CountColored(byte[] bgra)
            => CountPixels(bgra, (r, g, b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60);

        private static int CountPixels(byte[] bgra, Func<int, int, int, bool> predicate)
        {
            var count = 0;

            for (var i = 0; i < bgra.Length; i += 4)
            {
                if (predicate(bgra[i + 2], bgra[i + 1], bgra[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static void AssertSamePixels(byte[] expected, byte[] actual)
        {
            var (max, _, _) = Difference(expected, actual);

            Assert.True(max == 0, $"pixels differ by up to {max} levels");
        }
    }
}
