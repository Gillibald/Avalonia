using System;
using System.Collections.Generic;
using System.IO;
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
    /// End-to-end Phase 3 gates for the managed rasterization path, driven straight through
    /// <see cref="DrawingContextImpl.DrawGlyphRun"/>: cross-checks against the backend path
    /// (same scene, both engines, the render-test suite's RMSE bar), fallback identity for
    /// triage-rejected draws, and the D7/F0 zero-allocation warm-frame contract.
    /// </summary>
    public class ManagedGlyphRunRenderingTests
    {
        private const int Width = 240;
        private const int Height = 48;

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

        [Fact]
        public void Rotated_Draws_Fall_Back_To_The_Native_Blob_Identically()
        {
            using var _ = CreateEnvironment(out var typeface);

            var managed = RenderScene(typeface, TextRasterizationMode.Managed, rotate: true);
            var backend = RenderScene(typeface, TextRasterizationMode.Backend, rotate: true);

            // The triage rejects rotation, so the managed impl draws through its own native
            // blob — the same machinery as the backend impl, so the frames match near-exactly.
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
        public void Equal_Brush_Values_Reuse_The_Cached_Mask_While_A_Color_Change_Mints_A_Variant()
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

                // Two draws with distinct-but-equal black brushes share one cached variant
                // (value-keyed, not brush-identity-keyed); a red draw adds a second variant.
                context.DrawGlyphRun(Brushes.Black, run);
                context.DrawGlyphRun(new ImmutableSolidColorBrush(Colors.Black), run);

                Assert.True(IsTintCached(managedRun, Colors.Black));
                Assert.False(IsTintCached(managedRun, Colors.Red));

                context.DrawGlyphRun(Brushes.Red, run);

                Assert.True(IsTintCached(managedRun, Colors.Black));
                Assert.True(IsTintCached(managedRun, Colors.Red));
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

        private static byte[] RenderScene(GlyphTypeface typeface, TextRasterizationMode mode, bool rotate)
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

                context.DrawGlyphRun(Brushes.Black, run);

                return bitmap.GetPixelSpan().ToArray();
            }
            finally
            {
                run.Dispose();
            }
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
                ? new SkiaManagedGlyphRunImpl(typeface, emSize, infos, origin)
                : new GlyphRunImpl(typeface, emSize, infos, origin);
        }

        private static IDisposable CreateEnvironment(out GlyphTypeface typeface)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

            var bytes = LoadFontBytes("Inter-Regular.ttf");
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));
            Assert.NotNull(skTypeface);

            typeface = new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
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
