using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.Media.TextFormatting;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The Slug render wiring: triage decisions of <see cref="SlugGlyphRunRenderer"/> against a
    /// recording context, the GPU gate on real raster contexts, the run-artifact rebuild
    /// policy, and the production <see cref="ISlugGlyphRunContext.TryDrawSlugRun"/>
    /// implementation validated against the reference evaluator — called directly so the exact
    /// production matrix, apron, and tint math runs on a CPU canvas without a GrContext.
    /// </summary>
    public class SlugRenderWiringTests
    {
        private sealed class RecordingSlugContext : ISlugGlyphRunContext
        {
            public bool SupportsSlugRendering { get; set; } = true;

            public bool Result { get; set; } = true;

            public List<(int GlyphCount, uint Tint)> Draws { get; } = new();

            public bool TryDrawSlugRun(ManagedGlyphRunImpl run, SlugTexelStore store, uint tintArgb)
            {
                Draws.Add((run.GlyphCount, tintArgb));
                return Result;
            }
        }

        [Fact]
        public void Slug_Takes_Rotated_Runs_And_Hands_The_Whole_Run_To_The_Context()
        {
            var typeface = LoadTypeface();

            using var run = CreateRun(typeface, "go ");

            var context = new RecordingSlugContext();

            Assert.True(SlugGlyphRunRenderer.TryDraw(
                context, Matrix.CreateRotation(Math.PI / 6), run, Brushes.Black));

            // The whole run crosses the seam once; the renderer realized the two inked glyphs
            // (the space realizes as empty) before committing.
            Assert.Equal(1, context.Draws.Count);
            Assert.Equal((3, 0xFF000000u), context.Draws[0]);
            Assert.Equal(2, typeface.SlugStore.Version);

            // A context that fails to realize its resources declines the run, so the caller
            // falls back to its native path instead of rendering nothing.
            var failing = new RecordingSlugContext { Result = false };

            Assert.False(SlugGlyphRunRenderer.TryDraw(
                failing, Matrix.CreateRotation(Math.PI / 6), run, Brushes.Black));
        }

        [Fact]
        public void Unsupported_Contexts_And_Foregrounds_Decline()
        {
            var typeface = LoadTypeface();

            using var run = CreateRun(typeface, "go");

            var unsupported = new RecordingSlugContext { SupportsSlugRendering = false };

            Assert.False(SlugGlyphRunRenderer.TryDraw(unsupported, Matrix.Identity, run, Brushes.Black));
            Assert.Empty(unsupported.Draws);

            var supported = new RecordingSlugContext();

            Assert.False(SlugGlyphRunRenderer.TryDraw(supported, Matrix.Identity, run,
                new LinearGradientBrush()));
            Assert.False(SlugGlyphRunRenderer.TryDraw(supported, Matrix.Identity, run, null));
            Assert.Empty(supported.Draws);
        }


        [Fact]
        public void Transparent_Foregrounds_Are_Handled_Without_Draws()
        {
            var typeface = LoadTypeface();

            using var run = CreateRun(typeface, "go");

            var context = new RecordingSlugContext();

            Assert.True(SlugGlyphRunRenderer.TryDraw(context, Matrix.Identity, run, Brushes.Transparent));
            Assert.Empty(context.Draws);
        }

        [Fact]
        public void Raster_Drawing_Contexts_Report_No_Slug_Support()
        {
            var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            // No GrContext: the per-fragment evaluation belongs on a GPU, so the whole tier
            // stays cold here and rotated draws keep the native blob fallback.
            Assert.False(((ISlugGlyphRunContext)context).SupportsSlugRendering);
        }

        [Fact]
        public void The_Production_Draw_Matches_The_Reference_Evaluator_Under_Rotation()
        {
            const int width = 120;
            const int height = 120;
            const float emSize = 64f;
            const double baselineX = 40;
            const double baselineY = 90;
            const uint tint = 0x80FF0000;   // straight half-alpha red

            var typeface = LoadTypeface();
            var store = typeface.SlugStore;
            var glyph = typeface.CharacterToGlyphMap['g'];

            Assert.True(store.TryRealize(typeface, glyph, out var placement));
            Assert.True(placement.HorizontalBandCount > 0);

            using var run = CreateRun(typeface, "g", emSize, new Point(baselineX, baselineY));

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (Avalonia.Skia.DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(
                canvas, new Vector(96, 96));

            canvas.Clear(SKColors.Transparent);

            var transform = Matrix.CreateRotation(Math.PI / 6) * Matrix.CreateTranslation(40, -40);

            context.Transform = transform;

            Assert.True(((ISlugGlyphRunContext)context).TryDrawSlugRun(run, store, tint));

            // Recompute exactly what the draw derived: em → local, concatenated with the canvas
            // matrix, inverted for sampling and the fwidth-replacement footprints.
            var local = new SKMatrix(emSize, 0, (float)baselineX, 0, -emSize, (float)baselineY, 0, 0, 1);
            var emToDevice = SKMatrix.Concat(transform.ToSKMatrix(), local);

            Assert.True(emToDevice.TryInvert(out var deviceToEm));

            var emsPerPixelX = Math.Abs(deviceToEm.ScaleX) + Math.Abs(deviceToEm.SkewX);
            var emsPerPixelY = Math.Abs(deviceToEm.SkewY) + Math.Abs(deviceToEm.ScaleY);
            var alphaScale = (tint >> 24) / 255.0;

            var sum = 0.0;
            var worst = 0.0;
            var interiorChecked = false;

            // The production filter chain corrects coverage through the mask-gamma table for
            // the tint color (red) before the modulate applies the half alpha.
            var gammaTable = MaskGamma.GetTable(0xFF, 0x00, 0x00);

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var em = deviceToEm.MapPoint(new SKPoint(px + 0.5f, py + 0.5f));

                    var coverage = SlugReferenceEvaluator.Evaluate(
                        store.CurveTexels, store.BandTexels, in placement,
                        em.X, em.Y, emsPerPixelX, emsPerPixelY);

                    var corrected = gammaTable[(int)Math.Round(coverage * 255.0)] / 255.0;

                    var color = bitmap.GetPixel(px, py);
                    var delta = Math.Abs(color.Alpha / 255.0 - corrected * alphaScale);

                    sum += delta;
                    worst = Math.Max(worst, delta);

                    if (coverage > 0.9 && !interiorChecked)
                    {
                        // Unpremultiplied readback of a premultiplied red draw: interior pixels
                        // come back fully red, proving the tint premultiplied correctly.
                        Assert.True(color.Red >= 250, $"Interior pixel not red: {color}");
                        interiorChecked = true;
                    }
                }
            }

            var mean = sum / (width * height);

            Assert.True(interiorChecked, "The rotated glyph never reached interior coverage.");
            // The gamma table's steepest span roughly doubles a half-step quantization
            // delta, and the modulate adds one more rounding — hence the widened worst bound.
            Assert.True(mean <= 0.003 && worst <= 0.02,
                FormattableString.Invariant($"mean {mean:0.00000}, worst {worst:0.00000}"));
        }

        [Fact]
        public void The_Run_Artifact_Rebuilds_Only_When_The_Bucket_Or_The_Store_Moves()
        {
            var typeface = LoadTypeface();
            var store = typeface.SlugStore;

            using var run = CreateRun(typeface, "go");

            var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (Avalonia.Skia.DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(
                canvas, new Vector(96, 96));

            var slug = (ISlugGlyphRunContext)context;

            context.Transform = Matrix.CreateRotation(Math.PI / 18) * Matrix.CreateTranslation(50, 30);
            Assert.True(slug.TryDrawSlugRun(run, store, 0xFF000000));

            var artifact = Assert.IsType<Avalonia.Skia.SlugRunArtifact>(run.SlugRunArtifact);

            Assert.Equal(1, artifact.BuildCount);

            // Same transform and a pure translation keep the pixel-footprint bucket: no rebuild,
            // and a foreground change rides the paint filter without touching the artifact.
            Assert.True(slug.TryDrawSlugRun(run, store, 0xFF000000));
            context.Transform = Matrix.CreateRotation(Math.PI / 18) * Matrix.CreateTranslation(90, 60);
            Assert.True(slug.TryDrawSlugRun(run, store, 0xFF0000FF));
            Assert.Equal(1, artifact.BuildCount);

            // A zoom step crosses the bucket: one rebuild.
            context.Transform = Matrix.CreateRotation(Math.PI / 18) * Matrix.CreateScale(2, 2) *
                Matrix.CreateTranslation(50, 30);
            Assert.True(slug.TryDrawSlugRun(run, store, 0xFF000000));
            Assert.Equal(2, artifact.BuildCount);

            // New texels in the store (another glyph realized): one rebuild on the next draw.
            Assert.True(store.TryRealize(typeface, typeface.CharacterToGlyphMap['x'], out _));
            Assert.True(slug.TryDrawSlugRun(run, store, 0xFF000000));
            Assert.Equal(3, artifact.BuildCount);
        }

        private static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface, string text,
            double emSize = 16, Point? origin = null)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, origin ?? new Point(8, 32));
        }

        private static GlyphTypeface LoadTypeface()
        {
            var bytes = LoadFontBytes("Inter-Regular.ttf");
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
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
    }
}
