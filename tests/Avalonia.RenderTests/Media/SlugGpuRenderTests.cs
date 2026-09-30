using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SixLabors.ImageSharp.PixelFormats;
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
        private const double EmSize = 20;
        private const double Angle = 0.05;

        private static readonly Point s_origin = new(10, 30);

        public SlugGpuRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public async Task Rotated_Glyph_Run_Matches_The_Reference_Evaluator_On_Gpu()
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

            var run = new GlyphRun(typeface, EmSize, Text.AsMemory(), glyphs, s_origin);
            var target = new Border
            {
                Width = 260,
                Height = 50,
                Background = Brushes.White,
                Child = new RotatedGlyphRunControl(run),
            };

            await RenderToFile(target);

            var expected = Evaluate(typeface, run, 260, 50);

            if (MesaSoftwareRenderer.GlEnabled)
            {
                AssertMatches(expected, "composited.gles");
            }

            if (MesaSoftwareRenderer.VulkanEnabled)
            {
                AssertMatches(expected, "composited.vulkan");
            }
        }

        private void AssertMatches(double[,] expected, string outputType,
            [CallerMemberName] string testName = "")
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

        private static double[,] Evaluate(GlyphTypeface typeface, GlyphRun run, int width, int height)
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
                    var emToDevice = Matrix.CreateScale(EmSize, -EmSize) *
                        Matrix.CreateTranslation(penX, 0) * Matrix.CreateRotation(Angle) *
                        Matrix.CreateTranslation(s_origin.X, s_origin.Y);
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

        private sealed class RotatedGlyphRunControl : Control
        {
            private readonly GlyphRun _run;

            public RotatedGlyphRunControl(GlyphRun run) => _run = run;

            public override void Render(DrawingContext context)
            {
                using (context.PushTransform(Matrix.CreateTranslation(-s_origin.X, -s_origin.Y) *
                           Matrix.CreateRotation(Angle) * Matrix.CreateTranslation(s_origin.X, s_origin.Y)))
                {
                    context.DrawGlyphRun(Brushes.Black, _run);
                }
            }
        }
    }
}
