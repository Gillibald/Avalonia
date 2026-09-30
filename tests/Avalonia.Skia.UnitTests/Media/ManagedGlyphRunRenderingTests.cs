using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
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
    /// End-to-end Phase 3 gates for the managed rasterization path, driven straight through
    /// <see cref="DrawingContextImpl.DrawGlyphRun"/>: cross-checks against the backend path
    /// (same scene, both engines, the render-test suite's RMSE bar), fallback identity for
    /// triage-rejected draws, and the D7/F0 zero-allocation warm-frame contract.
    /// </summary>
    public class ManagedGlyphRunRenderingTests
    {
        private const int Width = 480;
        private const int Height = 96;

        [Fact]
        public void Managed_And_Backend_Paths_Render_The_Same_Scene_Within_Render_Test_Tolerance()
        {
            using var _ = CreateEnvironment(out var typeface);

            var managed = RenderScene(typeface, TextRasterizationMode.Managed, rotate: false);
            var backend = RenderScene(typeface, TextRasterizationMode.Backend, rotate: false);

            var rmse = Rmse(managed, backend);

            // Cross-ENGINE comparison, so the bar sits above the same-engine golden tolerance
            // (0.022): it deliberately carries the managed design deltas — quarter-pixel pen
            // snapping (up to 0.125 px per glyph vs the backend's exact subpixel placement),
            // Skia's text-mask contrast shaping, and the AA-model residue quantified in
            // planning/glyph-rasterizer-parity.md. Measured 0.033 at 16 px on this scene; the
            // gate has headroom for font/runtime drift but still fails instantly on placement,
            // scale, or color errors (a one-pixel shift alone measures far above 0.08).
            Assert.True(rmse <= 0.045, $"managed vs backend RMSE {rmse:0.0000} exceeds 0.045");
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        [InlineData(2.0)]
        public void Managed_And_Backend_Agree_Across_Device_Scales(double scale)
        {
            using var scope = CreateEnvironment(out var typeface);

            // Each scale hits a different mask size bucket; agreement across the matrix verifies
            // the DPI story end to end (triage scale extraction, bucket quantization, placement).
            var managed = RenderScene(typeface, TextRasterizationMode.Managed, rotate: false, scale);
            var backend = RenderScene(typeface, TextRasterizationMode.Backend, rotate: false, scale);

            var rmse = Rmse(managed, backend);

            Assert.True(rmse <= 0.045, $"scale {scale}: managed vs backend RMSE {rmse:0.0000} exceeds 0.045");
        }

        [Theory]
        [InlineData(FontSimulations.Bold, 1.0)]
        [InlineData(FontSimulations.Oblique, 1.0)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, 1.0)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, 2.0)]
        public void Managed_Simulations_Shear_And_Embolden_The_Regular_Rendering(FontSimulations simulations,
            double scale)
        {
            const double emSize = 20;

            using var scope = CreateEnvironment(out var regular);

            var expected = RenderStems(regular, emSize, scale);
            var actual = RenderStems(regular.WithSimulations(simulations), emSize, scale);

            // Fake bold widens every stem by one stroke, which follows the documented size
            // table: 1/24 of the em size at 9 px and below, 1/32 at 36 px and above, linear in
            // between, taken at the layout em size and applied in device pixels.
            var ratio = emSize <= 9 ? 1.0 / 24
                : emSize >= 36 ? 1.0 / 32
                : 1.0 / 24 + (emSize - 9) / (36 - 9) * (1.0 / 32 - 1.0 / 24);
            var stroke = (simulations & FontSimulations.Bold) != 0 ? emSize * ratio * scale : 0;

            // Oblique shears each row by its height above the baseline; the embolden grows a
            // stem symmetrically and leaves its centre where it was.
            var slant = (simulations & FontSimulations.Oblique) != 0 ? FontSimulationConstants.ObliqueSlant : 0;

            var baseline = StemsBaseline * scale;
            var xHeight = InterXHeight * emSize * scale;
            var failures = new List<string>();
            var rows = 0;

            for (var y = 0; y < Height; y++)
            {
                var height = baseline - (y + 0.5);

                // The straight part of the stems only: bowls, shoulders and stem ends change
                // their horizontal extent under an embolden in ways a row cannot isolate.
                if (height < 0.3 * xHeight || height > 0.6 * xHeight)
                {
                    continue;
                }

                var regularStems = Spans(expected, y);
                var simulatedStems = Spans(actual, y);

                rows++;

                if (regularStems.Count != simulatedStems.Count)
                {
                    failures.Add($"row {y}: {simulatedStems.Count} stems instead of {regularStems.Count}");
                    continue;
                }

                for (var i = 0; i < regularStems.Count; i++)
                {
                    var widened = simulatedStems[i].Ink - regularStems[i].Ink;
                    var shifted = simulatedStems[i].Centroid - regularStems[i].Centroid;

                    if (Math.Abs(widened - stroke) > 0.1 || Math.Abs(shifted - slant * height) > 0.2)
                    {
                        failures.Add(FormattableString.Invariant(
                            $"row {y} stem {i}: {widened:0.00} px wider (table {stroke:0.00}), moved {shifted:0.00} px (slant {slant * height:0.00})"));
                    }
                }
            }

            Assert.True(rows >= 2 * scale, $"only {rows} rows measured");
            Assert.True(failures.Count == 0, $"{simulations} at scale {scale}:{Environment.NewLine}" +
                string.Join(Environment.NewLine, failures));
        }

        [Theory]
        [InlineData(FontSimulations.None, false)]
        [InlineData(FontSimulations.Bold, false)]
        [InlineData(FontSimulations.Oblique, false)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, false)]
        [InlineData(FontSimulations.None, true)]
        [InlineData(FontSimulations.Bold, true)]
        [InlineData(FontSimulations.Oblique, true)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, true)]
        public void Simulated_Variants_Render_The_Same_After_Their_Siblings(FontSimulations simulations, bool rotate)
        {
            byte[] cold;

            // Rotated, the run takes the transformed mask tier, whose masks share the cache too.
            using (CreateEnvironment(out var typeface, simulations))
            {
                cold = RenderScene(typeface, TextRasterizationMode.Managed, rotate);
            }

            using (CreateEnvironment(out var regular))
            {
                // Every sibling draws the same glyphs at the same size first, so a glyph mask
                // any of them leaves behind for the others must not be mistaken for this one.
                foreach (var sibling in new[]
                         {
                             FontSimulations.None, FontSimulations.Bold, FontSimulations.Oblique,
                             FontSimulations.Bold | FontSimulations.Oblique,
                         })
                {
                    if (sibling != simulations)
                    {
                        RenderScene(regular.WithSimulations(sibling), TextRasterizationMode.Managed, rotate);
                    }
                }

                var warm = RenderScene(regular.WithSimulations(simulations), TextRasterizationMode.Managed, rotate);

                Assert.True(cold.AsSpan().SequenceEqual(warm),
                    $"{simulations}{(rotate ? ", rotated" : "")}: the warm frame differs");
            }
        }

        [Theory]
        [InlineData(FontSimulations.None)]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Rotated_Varied_Runs_Draw_Their_Own_Instance(FontSimulations simulations)
        {
            using var scope = CreateVariedEnvironment(out var typeface, simulations, managed: true);

            // The mask path draws the run axis-aligned. Rotated and painted with a gradient, both
            // mask tiers decline it and the run falls back past them; the fallback must still
            // draw the wght=900 outlines, and rotation keeps the total ink, while the default
            // instance (wght=400) has little more than half of it. Aliased, so that coverage is
            // the same measure for text masks and filled paths.
            var masked = RenderVaried(typeface, aliased: true, (context, run) =>
                context.DrawGlyphRun(Brushes.Black, run.PlatformImpl.Item));
            var rotated = RenderVaried(typeface, aliased: true, (context, run) =>
            {
                context.Transform = Matrix.CreateRotation(0.2) * Matrix.CreateTranslation(10, -14);
                context.DrawGlyphRun(GradientBlack, run.PlatformImpl.Item);
            });

            var inkRatio = Ink(rotated) / Ink(masked);

            Assert.True(Math.Abs(inkRatio - 1) <= 0.06,
                $"{simulations}: rotated ink is {inkRatio:0.000} of the axis-aligned run's");
        }

        [Theory]
        [InlineData(FontSimulations.None)]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Backend_Mode_Draws_Varied_Runs_At_Their_Instance(FontSimulations simulations)
        {
            byte[] managed;
            byte[] backend;

            using (CreateVariedEnvironment(out var typeface, simulations, managed: true))
            {
                managed = RenderVaried(typeface, aliased: false, (context, run) =>
                    context.DrawGlyphRun(Brushes.Black, run.PlatformImpl.Item));
            }

            // Skia cannot vary a typeface, so a backend glyph run of a varied clone would draw
            // the default instance.
            using (CreateVariedEnvironment(out var typeface, simulations, managed: false))
            {
                backend = RenderVaried(typeface, aliased: false, (context, run) =>
                    context.DrawGlyphRun(Brushes.Black, run.PlatformImpl.Item));
            }

            AssertSameInstance(managed, backend, simulations);
        }

        [Theory]
        [InlineData(FontSimulations.None)]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Varied_Run_Geometry_Has_The_Instance_Outlines(FontSimulations simulations)
        {
            using var scope = CreateVariedEnvironment(out var typeface, simulations, managed: true);

            // Aliased: an antialiased path fill has no text contrast shaping and carries about a
            // fifth more ink than a text mask of the same outlines.
            var masked = RenderVaried(typeface, aliased: true, (context, run) =>
                context.DrawGlyphRun(Brushes.Black, run.PlatformImpl.Item));
            var filled = RenderVaried(typeface, aliased: true, (context, run) =>
                context.DrawGeometry(Brushes.Black, null, new PlatformRenderInterface().BuildGlyphRunGeometry(run)));

            AssertSameInstance(masked, filled, simulations);
        }

        /// <summary>
        /// A black foreground that is not a solid colour brush. Neither mask tier takes a
        /// non-solid foreground, so a run painted with it reaches the backend fallback.
        /// </summary>
        private static IBrush GradientBlack => new LinearGradientBrush
        {
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1) },
        };

        private static void AssertSameInstance(byte[] expected, byte[] actual, FontSimulations simulations)
        {
            var rmse = Rmse(expected, actual);

            Assert.True(rmse <= 0.045, $"{simulations}: RMSE {rmse:0.0000} exceeds 0.045");

            // The default instance sits in almost the same place with far less weight, which a
            // mostly white frame hides from the RMSE; the total ink does not.
            var inkRatio = Ink(actual) / Ink(expected);

            Assert.True(Math.Abs(inkRatio - 1) <= 0.06, $"{simulations}: ink is {inkRatio:0.000} of the reference");
        }

        private static byte[] RenderVaried(GlyphTypeface typeface, bool aliased,
            Action<DrawingContextImpl, GlyphRun> draw)
        {
            const double emSize = 16;
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in "Managed glyphs 123")
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            using var run = new GlyphRun(typeface, emSize, default, infos, new Point(8, 32));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);
            context.PushTextOptions(new TextOptions
            {
                TextHintingMode = TextHintingMode.None,
                TextRenderingMode = aliased ? TextRenderingMode.Alias : TextRenderingMode.Antialias,
            });
            context.PushRenderOptions(new RenderOptions
            {
                EdgeMode = aliased ? EdgeMode.Aliased : EdgeMode.Antialias,
            });

            draw(context, run);

            return bitmap.GetPixelSpan().ToArray();
        }

        private static IDisposable CreateVariedEnvironment(out GlyphTypeface typeface, FontSimulations simulations,
            bool managed)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = managed ? TextRasterizationMode.Managed : TextRasterizationMode.Backend,
                });

            var bytes = LoadFontBytes("InterVariable.ttf");
            Assert.True(SfntFace.TryLoad(new MemoryStream(bytes), out var face));

            typeface = new GlyphTypeface(face)
                .WithVariations(FontVariationSettings.Parse("wght=900"))
                .WithSimulations(simulations);

            return scope;
        }

        [Fact]
        public void Managed_Run_Bounds_Cover_Simulated_Ink()
        {
            using var regularScope = CreateEnvironment(out var regular);
            using var regularRun = CreateRun(regular, TextRasterizationMode.Managed);

            using var simulatedScope = CreateEnvironment(out var simulated,
                FontSimulations.Bold | FontSimulations.Oblique);
            using var simulatedRun = CreateRun(simulated, TextRasterizationMode.Managed);

            // Emboldening grows the ink on every side and the slant pushes ascenders right, so
            // the dirty region of the simulated run must be larger in both directions.
            Assert.True(simulatedRun.Bounds.Top < regularRun.Bounds.Top);
            Assert.True(simulatedRun.Bounds.Bottom > regularRun.Bounds.Bottom);
            Assert.True(simulatedRun.Bounds.Right > regularRun.Bounds.Right + 1);

            foreach (var simulations in new[]
                     {
                         FontSimulations.Bold, FontSimulations.Oblique, FontSimulations.Bold | FontSimulations.Oblique,
                     })
            {
                using var scope = CreateEnvironment(out var typeface, simulations);
                using var run = CreateRun(typeface, TextRasterizationMode.Managed);

                // Every pixel the run inks lies inside its reported bounds, which antialiasing
                // may bleed past by up to a pixel.
                var covered = run.Bounds.Inflate(1);
                var pixels = RenderScene(typeface, TextRasterizationMode.Managed, rotate: false);

                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var i = (y * Width + x) * 4;

                        if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
                        {
                            Assert.True(covered.Contains(new Point(x + 0.5, y + 0.5)),
                                $"{simulations}: ink at ({x}, {y}) lies outside the run bounds {run.Bounds}");
                        }
                    }
                }
            }
        }

        [Fact]
        public void Managed_Analytic_Intersections_Match_The_Backend_Within_Tolerance()
        {
            using var scope = CreateEnvironment(out var typeface);

            using var managed = (ManagedGlyphRunImpl)CreateRun(typeface, TextRasterizationMode.Managed);
            using var backend = (GlyphRunImpl)CreateRun(typeface, TextRasterizationMode.Backend);

            // The managed intercepts come from our own flattened outlines rather than the
            // native blob; decoration ink-skipping needs matching gap structure with boundary
            // positions within a fraction of a pixel (flattening tolerance + engine rounding).
            // Bands are baseline-relative, matching the blob contract: below the baseline for
            // underlines, above (negative) for strikethrough.
            foreach (var (lower, upper) in new[] { (2f, 5f), (-6f, -4f) })
            {
                var managedHits = managed.GetIntersections(lower, upper);
                var backendHits = backend.GetIntersections(lower, upper);

                Assert.Equal(backendHits.Count, managedHits.Count);

                for (var i = 0; i < backendHits.Count; i++)
                {
                    Assert.True(Math.Abs(backendHits[i] - managedHits[i]) <= 0.75f,
                        $"band ({lower},{upper}) boundary {i}: managed {managedHits[i]} vs backend {backendHits[i]}");
                }
            }
        }

        [Fact]
        public void Analytic_Intersections_Report_Descender_Ink_Only()
        {
            using var scope = CreateEnvironment(out var typeface);

            // A baseline-relative underline band: ascender-only text reports no ink to skip,
            // descenders report intervals.
            using var ascenders = (ManagedGlyphRunImpl)CreateBaseRun(typeface, "ill");
            using var descenders = (ManagedGlyphRunImpl)CreateBaseRun(typeface, "gjp");

            Assert.Empty(ascenders.GetIntersections(2f, 5f));
            Assert.True(descenders.GetIntersections(2f, 5f).Count >= 2);
        }

        [Fact]
        public void Rotated_Gradient_Draws_Fall_Back_To_The_Native_Blob_Identically()
        {
            using var _ = CreateEnvironment(out var typeface);

            var managed = RenderScene(typeface, TextRasterizationMode.Managed, rotate: true, gradient: true);
            var backend = RenderScene(typeface, TextRasterizationMode.Backend, rotate: true, gradient: true);

            // Both mask tiers reject a non-solid foreground, so the managed impl draws through
            // its own native blob — the same machinery as the backend impl, so the frames match
            // near-exactly.
            var rmse = Rmse(managed, backend);

            Assert.True(rmse <= 0.001, $"fallback vs backend RMSE {rmse:0.0000} exceeds 0.001");
        }

        [Fact]
        public void A_Warm_Managed_Frame_Allocates_Nothing()
        {
            using var _ = CreateEnvironment(out var typeface);

            var run = CreateRun(typeface, TextRasterizationMode.Managed);

            try
            {
                var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                using var canvas = new SKCanvas(bitmap);
                using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

                // Cold draw composes and caches the run mask.
                context.DrawGlyphRun(Brushes.Black, run);

                // Warm-up a second time so pools, paint caches and the Skia image wrap settle.
                context.DrawGlyphRun(Brushes.Black, run);

                var before = GC.GetAllocatedBytesForCurrentThread();

                for (var i = 0; i < 100; i++)
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.True(allocated == 0, $"100 warm managed draws allocated {allocated} bytes");
            }
            finally
            {
                run.Dispose();
            }
        }

        [Fact]
        public void Cpu_Contexts_Keep_Tinted_Masks_And_The_Alpha_Implementation_Stays_Correct()
        {
            using var scope = CreateEnvironment(out var typeface);

            var run = CreateRun(typeface, TextRasterizationMode.Managed);

            try
            {
                var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                using var canvas = new SKCanvas(bitmap);
                using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

                var managedRun = (ManagedGlyphRunImpl)run;
                var alphaContext = (IAlphaGlyphMaskContext)context;

                // A raster canvas reports no alpha-mask preference (the CPU pipeline draws
                // color-modulated A8 ~6x slower than pre-tinted BGRA), so the renderer keeps
                // per-color tinted variants here; only GPU contexts take the untinted path.
                Assert.False(alphaContext.PrefersAlphaMasks);

                context.DrawGlyphRun(Brushes.Black, run);
                context.DrawGlyphRun(Brushes.Red, run);

                Assert.True(IsTintCached(managedRun, Colors.Black));
                Assert.True(IsTintCached(managedRun, Colors.Red));

                var alphaKey = new RunMaskKey(GlyphMaskKey.QuantizeScale(16f), 0, GlyphMaskMode.Antialiased, 0u);
                Assert.False(managedRun.RunMasks.TryGet(alphaKey, out _));

                // The implementation itself works on any context (the GPU path relies on it):
                // a full-coverage mask tinted green must land green pixels.
                var maskBytes = new byte[8 * 8];
                maskBytes.AsSpan().Fill(255);

                using var handle = alphaContext.CreateAlphaMask(maskBytes, 8, 8);
                alphaContext.DrawAlphaMask(handle, new Rect(0, 0, 8, 8), new Rect(200, 40, 8, 8), 0xFF00FF00);

                var pixels = bitmap.GetPixelSpan();
                var index = (44 * Width + 204) * 4;
                Assert.True(pixels[index + 1] > 200 && pixels[index + 2] < 60,
                    "the drawn alpha mask did not render with its green tint");
            }
            finally
            {
                run.Dispose();
            }
        }

        private static bool IsTintCached(ManagedGlyphRunImpl run, Color color)
        {
            var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale(16f), 0, GlyphMaskMode.Antialiased, tint);

            return run.RunMasks.TryGet(key, out _);
        }

        private static byte[] RenderScene(GlyphTypeface typeface, TextRasterizationMode mode, bool rotate,
            double scale = 1.0, bool gradient = false)
        {
            var run = CreateRun(typeface, mode);

            try
            {
                var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                using var canvas = new SKCanvas(bitmap);
                using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

                canvas.Clear(SKColors.White);

                // Hinting off for the comparison: the managed path is unhinted by design (a
                // documented non-goal), so the apples-to-apples check disables the backend's
                // hinting too — the same configuration the parity harness measured. The visual
                // delta of hinted-backend-vs-unhinted-managed is the known trade-off recorded in
                // the plan, judged by the Phase 6 visual review, not by this gate.
                context.PushTextOptions(new TextOptions { TextHintingMode = TextHintingMode.None });

                if (rotate)
                {
                    context.Transform = Matrix.CreateRotation(0.2) * Matrix.CreateTranslation(10, 6);
                }
                else if (scale != 1.0)
                {
                    context.Transform = Matrix.CreateScale(scale, scale);
                }

                IBrush foreground = gradient
                    ? new LinearGradientBrush
                    {
                        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.DarkBlue, 1) },
                    }
                    : Brushes.Black;

                context.DrawGlyphRun(foreground, run);

                return bitmap.GetPixelSpan().ToArray();
            }
            finally
            {
                run.Dispose();
            }
        }

        private const double StemsBaseline = 32;

        // Inter's x-height is about 0.55 em.
        private const double InterXHeight = 0.55;

        /// <summary>
        /// Upright stems between the baseline and the x-height, drawn through the managed mask
        /// path without hinting.
        /// </summary>
        private static byte[] RenderStems(GlyphTypeface typeface, double emSize, double scale)
        {
            var designScale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in "l m n l m n l m n")
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * designScale));
            }

            using var run = new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(8, StemsBaseline));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);
            context.PushTextOptions(new TextOptions { TextHintingMode = TextHintingMode.None });
            context.Transform = Matrix.CreateScale(scale, scale);
            context.DrawGlyphRun(Brushes.Black, run);

            return bitmap.GetPixelSpan().ToArray();
        }

        /// <summary>
        /// The ink spans of row <paramref name="y"/> of black text on white, each with its
        /// coverage in pixels and its horizontal centroid. Coverage is read back through the
        /// inverse of the text contrast table, so that it is linear in the covered area.
        /// </summary>
        private static List<(double Ink, double Centroid)> Spans(byte[] pixels, int y)
        {
            var table = MaskGamma.GetTable(0, 0, 0);
            var spans = new List<(double Ink, double Centroid)>();
            double ink = 0, moment = 0;

            for (var x = 0; x <= Width; x++)
            {
                var coverage = 0.0;

                if (x < Width)
                {
                    var shaded = 255 - pixels[(y * Width + x) * 4 + 1];
                    var linear = 0;

                    while (linear < 255 && table[linear] < shaded)
                    {
                        linear++;
                    }

                    coverage = linear / 255.0;
                }

                if (coverage > 0)
                {
                    ink += coverage;
                    moment += coverage * (x + 0.5);
                }
                else if (ink > 0)
                {
                    spans.Add((ink, moment / ink));
                    ink = moment = 0;
                }
            }

            return spans;
        }

        private static IGlyphRunImpl CreateBaseRun(GlyphTypeface typeface, string text = "Managed glyphs 123")
        {
            const double emSize = 16;
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(8, 32));
        }

        private static IGlyphRunImpl CreateRun(GlyphTypeface typeface, TextRasterizationMode mode)
        {
            const double emSize = 16;
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in "Managed glyphs 123")
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            var origin = new Point(8, 32);

            return mode == TextRasterizationMode.Managed
                ? new ManagedGlyphRunImpl(typeface, emSize, infos, origin)
                : new GlyphRunImpl(typeface, emSize, infos, origin);
        }

        private static IDisposable CreateEnvironment(out GlyphTypeface typeface,
            FontSimulations simulations = FontSimulations.None)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

            var bytes = LoadFontBytes("Inter-Regular.ttf");
            Assert.True(SfntFace.TryLoad(new MemoryStream(bytes), out var face));

            typeface = new GlyphTypeface(face).WithSimulations(simulations);
            return scope;
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

        /// <summary>Total coverage of black text on white, summed over the color channels.</summary>
        private static double Ink(byte[] pixels)
        {
            double sum = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                sum += 3 * 255 - pixels[i] - pixels[i + 1] - pixels[i + 2];
            }

            return sum / 255.0;
        }

        private static double Rmse(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);

            double sum = 0;

            for (var i = 0; i < a.Length; i++)
            {
                var d = (a[i] - b[i]) / 255.0;
                sum += d * d;
            }

            return Math.Sqrt(sum / a.Length);
        }
    }
}
