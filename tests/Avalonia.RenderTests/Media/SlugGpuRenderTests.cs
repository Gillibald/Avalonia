using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.Rendering.SceneGraph;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// Checks the Slug vector tier on the Mesa GPU outputs pixel by pixel against the reference
    /// evaluator, which models the pixel shader on the CPU. Needs no golden: any pixel where the
    /// GPU and the model disagree past the misclassification line is a defect in how the shader
    /// runs, not an approximation difference.
    /// </summary>
    public class SlugGpuRenderTests : TestBase
    {
        private const string Text = "Hamburgefonstiv 0123";
        private const double Angle = 0.05;

        public SlugGpuRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public Task Rotated_Glyph_Run_Matches_The_Reference_Evaluator_On_Gpu()
            => AssertRotatedRunMatches(20, new Point(10, 30), 260, 50);

        [Fact]
        public Task Rotated_Large_Glyph_Run_Matches_The_Reference_Evaluator_On_Gpu()
        {
            // Past 32 px per em each pixel walks only its side of the split band lists, so the
            // run start and ray direction vary per pixel inside a 2x2 quad.
            return AssertRotatedRunMatches(48, new Point(10, 60), 620, 110);
        }

        [Fact]
        public async Task Closed_Form_Gamma_Filter_Matches_The_Mask_Gamma_Tables()
        {
            // One coverage ramp per luminance bucket through the Slug tint filter, on the CPU
            // renderers and both Mesa GPU outputs; MaskGamma's tables are the specification.
            var target = new GammaRampControl { Width = 256, Height = MaskGamma.BucketCount };

            await RenderToFile(target);

            AssertRampMatches("immediate");
            AssertRampMatches("composited");

            if (MesaSoftwareRenderer.GlEnabled)
            {
                AssertRampMatches("composited.gles");
            }

            if (MesaSoftwareRenderer.VulkanEnabled)
            {
                AssertRampMatches("composited.vulkan");
            }
        }

        private void AssertRampMatches(string outputType,
            [CallerMemberName] string testName = "")
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(Path.Combine(OutputPath, $"{testName}.{outputType}.out.png"));

            var maxError = 0;
            var worst = "";

            for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
            {
                var table = MaskGamma.GetTable(bucket);

                for (var coverage = 0; coverage < 256; coverage++)
                {
                    var actual = image[coverage, bucket].A;
                    var error = Math.Abs(actual - table[coverage]);

                    if (error > maxError)
                    {
                        maxError = error;
                        worst = FormattableString.Invariant(
                            $" Worst: bucket {bucket}, coverage {coverage}: table {table[coverage]}, filter {actual}.");
                    }
                }

                // The tables pin both endpoints: zero coverage never leaks ink, full stays opaque.
                Assert.True(image[0, bucket].A == 0 && image[255, bucket].A == 255,
                    $"{outputType}: bucket {bucket} endpoints are {image[0, bucket].A} and {image[255, bucket].A}.");
            }

            Xunit.TestContext.Current.TestOutputHelper?.WriteLine($"{outputType}: max error {maxError}.{worst}");

            Assert.True(maxError <= 1, $"{outputType}: max error {maxError} levels.{worst}");
        }

        private async Task AssertRotatedRunMatches(double emSize, Point origin, int width, int height,
            [CallerMemberName] string testName = "")
        {
            // Rotation keeps the run off the mask path, so the GPU outputs draw it through Slug.
            // Heavy stems put many pixels deep inside the ink, where a lost sample shows as a hole.
            var path = Path.Combine(TestRenderHelper.GetTestsDirectory(), "Avalonia.RenderTests", "Assets",
                "Inter-Bold.ttf");

            Assert.True(SfntFace.TryLoad(path, 0, out var face));

            var typeface = new GlyphTypeface(face);
            var glyphs = new ushort[Text.Length];

            for (var i = 0; i < Text.Length; i++)
            {
                glyphs[i] = typeface.CharacterToGlyphMap[Text[i]];
            }

            var run = new GlyphRun(typeface, emSize, Text.AsMemory(), glyphs, origin);
            var target = new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                Child = new RotatedGlyphRunControl(run, origin),
            };

            await RenderToFile(target, testName);

            var expected = Evaluate(typeface, run, emSize, origin, width, height);

            if (MesaSoftwareRenderer.GlEnabled)
            {
                AssertMatches(expected, "composited.gles", testName);
            }

            if (MesaSoftwareRenderer.VulkanEnabled)
            {
                AssertMatches(expected, "composited.vulkan", testName);
            }
        }

        private void AssertMatches(double[,] expected, string outputType, string testName)
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(Path.Combine(OutputPath, $"{testName}.{outputType}.out.png"));

            var mismatches = 0;
            var first = "";

            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    // Black ink over white: the red channel carries 1 - coverage.
                    var gpu = 1 - image[x, y].R / 255.0;

                    if (Math.Abs(gpu - expected[x, y]) > 0.5)
                    {
                        if (mismatches++ == 0)
                        {
                            first = FormattableString.Invariant(
                                $" First at ({x}, {y}): GPU {gpu:0.00}, evaluator {expected[x, y]:0.00}.");
                        }
                    }
                }
            }

            Assert.True(mismatches == 0, $"{outputType}: {mismatches} pixels disagree with the evaluator.{first}");
        }

        private static double[,] Evaluate(GlyphTypeface typeface, GlyphRun run, double emSize, Point origin,
            int width, int height)
        {
            var store = typeface.SlugStore;
            var coverage = new double[width, height];
            var gamma = MaskGamma.GetTable(0, 0, 0);
            var penX = 0.0;

            foreach (var info in run.GlyphInfos)
            {
                Assert.True(store.TryRealize(typeface, info.GlyphIndex, out var placement));

                if (placement.HorizontalBandCount != 0)
                {
                    var emToDevice = Matrix.CreateScale(emSize, -emSize) *
                        Matrix.CreateTranslation(penX, 0) * Matrix.CreateRotation(Angle) *
                        Matrix.CreateTranslation(origin.X, origin.Y);
                    var deviceToEm = emToDevice.Invert();

                    // The production draw bakes the footprint quantized to the mask-cache grid.
                    var emsPerPixelX = GlyphMaskKey.ScaleQuantum / (float)GlyphMaskKey.QuantizeScale(
                        (float)(1 / (Math.Abs(deviceToEm.M11) + Math.Abs(deviceToEm.M21))));
                    var emsPerPixelY = GlyphMaskKey.ScaleQuantum / (float)GlyphMaskKey.QuantizeScale(
                        (float)(1 / (Math.Abs(deviceToEm.M12) + Math.Abs(deviceToEm.M22))));

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var em = new Point(x + 0.5, y + 0.5).Transform(deviceToEm);

                            if (em.X < placement.MinX - 0.1 || em.X > placement.MaxX + 0.1 ||
                                em.Y < placement.MinY - 0.1 || em.Y > placement.MaxY + 0.1)
                            {
                                continue;
                            }

                            var value = SlugReferenceEvaluator.Evaluate(store.CurveTexels, store.BandTexels,
                                in placement, (float)em.X, (float)em.Y, emsPerPixelX, emsPerPixelY);

                            coverage[x, y] = Math.Max(coverage[x, y], gamma[(int)Math.Round(value * 255.0)] / 255.0);
                        }
                    }
                }

                penX += info.GlyphAdvance;
            }

            return coverage;
        }

        private sealed class GammaRampControl : Control
        {
            public override void Render(DrawingContext context)
                => context.Custom(new GammaRampOperation(new Rect(Bounds.Size)));
        }

        private sealed class GammaRampOperation : ICustomDrawOperation
        {
            public GammaRampOperation(Rect bounds) => Bounds = bounds;

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation? other) => false;

            public void Dispose()
            {
            }

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();

                Assert.NotNull(feature);

                using var lease = feature!.Lease();

                // The Slug shaders emit coverage as premultiplied white, so every channel is alpha.
                var info = new SKImageInfo(256, MaskGamma.BucketCount, SKColorType.Rgba8888, SKAlphaType.Premul);
                var ramp = new byte[info.BytesSize];

                for (var i = 0; i < ramp.Length; i++)
                {
                    ramp[i] = (byte)(i / 4 % 256);
                }

                using var image = SKImage.FromPixelCopy(info, ramp, info.RowBytes);

                for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
                {
                    // A gray whose luma lands mid-bucket, so the row exercises exactly that bucket.
                    var gray = (uint)(bucket * 32 + 16);

                    using var artifact = new SlugRunArtifact();
                    using var paint = new SKPaint();

                    paint.BlendMode = SKBlendMode.Src;
                    paint.ColorFilter = artifact.GetFilter(0xFF000000 | gray << 16 | gray << 8 | gray);

                    var row = new SKRect(0, bucket, 256, bucket + 1);

                    lease.SkCanvas.DrawImage(image, row, row, new SKSamplingOptions(), paint);
                }
            }
        }

        private sealed class RotatedGlyphRunControl : Control
        {
            private readonly GlyphRun _run;
            private readonly Point _origin;

            public RotatedGlyphRunControl(GlyphRun run, Point origin)
            {
                _run = run;
                _origin = origin;
            }

            public override void Render(DrawingContext context)
            {
                using (context.PushTransform(Matrix.CreateTranslation(-_origin.X, -_origin.Y) *
                           Matrix.CreateRotation(Angle) * Matrix.CreateTranslation(_origin.X, _origin.Y)))
                {
                    context.DrawGlyphRun(Brushes.Black, _run);
                }
            }
        }
    }
}
