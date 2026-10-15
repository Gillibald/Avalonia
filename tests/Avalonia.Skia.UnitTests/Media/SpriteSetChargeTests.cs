using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A sprite set is charged to the glyph cache budget for what it keeps: its sprites, and its
    /// batches' objects, sprite arrays and the vertices a stretched draw keeps across frames.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class SpriteSetChargeTests
    {
        // Per sprite: the laid-out sprite and the batch's source rectangle and placement (32 bytes
        // each), and the vertices a stretched draw keeps, four positions and four texture
        // coordinates of 8 bytes and six 16-bit indices (76 bytes).
        private const long MinStretchedSpriteBytes = 32 + 32 + 76;

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
            yield return new object[] { GpuBackend.Vulkan };
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Crossing_Set_Drawn_Stretched_Is_Charged_For_Its_Kept_Vertices(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(520, 360, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            using var run = WideRunMaskTests.CreateRun(typeface, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJ", 13,
                new Point(6.3, 17));

            TestRasterTuning.Set(t => t with { MotionBudgetMicroseconds = 0 });

            static double Scale(int frame) => 1 + frame * 0.04;

            var scales = Enumerable.Range(0, 20).Select(Scale).ToList();
            var crossing = TransformedAtlasTests.GridCrossingFrames(scales, run, 0)[0];
            var state = run.TransformedSprites;

            for (var frame = 0; frame < crossing; frame++)
            {
                Draw(context, run, scales[frame]);
            }

            var before = state.ByteCost;

            Draw(context, run, scales[crossing]);

            var set = state.Settled;

            Assert.True(set is { IsCrossing: true }, "the crossing frame settled no crossing set");

            var charged = state.ByteCost - before;

            Assert.True(charged >= set!.Count * MinStretchedSpriteBytes,
                $"a crossing of {set.Count} sprites drawn stretched was charged {charged} bytes");
        }

        private static void Draw(DrawingContextImpl context, ManagedGlyphRunImpl run, double scale)
        {
            context.Transform = Matrix.CreateScale(scale, scale);
            context.DrawGlyphRun(Brushes.Black, run);
            context.FlushGlyphBatch();
        }
    }
}
