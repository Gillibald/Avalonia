using System;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The batch a run began, recorded so the same batch drawn again reuses its vertices, keeps
    /// no other run of it reachable: a run released while the first one is still drawn leaves the
    /// memory of its sprites to the collector.
    /// </summary>
    public class GlyphAtlasBatchRecordTests
    {
        [Fact]
        public void A_Recorded_Batch_Keeps_None_Of_Its_Other_Runs_Reachable()
        {
            var first = CreateBatch(4);
            var other = RecordWithAnotherRun(first);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(other.IsAlive, "the first run's recorded batch keeps another run's sprites alive");
            GC.KeepAlive(first);
        }

        [Fact]
        public void A_Batch_Of_The_Same_Runs_At_The_Same_Offsets_Still_Repeats()
        {
            var first = CreateBatch(4);
            var second = CreateBatch(3);
            var runs = new[]
            {
                new DrawingContextImpl.BatchedRun(first, 10, 20, SKColors.Black),
                new DrawingContextImpl.BatchedRun(second, 40, 20, SKColors.Black),
            };

            Assert.False(first.TryGetBatchVertices(runs, 2, 7, colored: false, required: false, out _, out _));

            // Moved together, as a scrolled line moves.
            runs[0] = runs[0] with { X = 15, Y = 32 };
            runs[1] = runs[1] with { X = 45, Y = 32 };

            Assert.True(first.TryGetBatchVertices(runs, 2, 7, colored: false, required: false, out var vertices,
                out var built));
            Assert.True(built);
            Assert.NotEmpty(vertices);

            // A different second run does not repeat the batch.
            runs[1] = runs[1] with { Backend = CreateBatch(3) };

            Assert.False(first.TryGetBatchVertices(runs, 2, 7, colored: false, required: false, out _, out _));

            first.Dispose();
            second.Dispose();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RecordWithAnotherRun(DrawingContextImpl.SkiaGlyphAtlasBatch first)
        {
            var other = CreateBatch(3);
            var runs = new[]
            {
                new DrawingContextImpl.BatchedRun(first, 0, 0, SKColors.Black),
                new DrawingContextImpl.BatchedRun(other, 30, 0, SKColors.Black),
            };

            first.TryGetBatchVertices(runs, 2, 7, colored: false, required: false, out _, out _);

            return new WeakReference(other);
        }

        private static DrawingContextImpl.SkiaGlyphAtlasBatch CreateBatch(int sprites)
        {
            var sources = new SKRect[sprites];
            var placements = new SKRotationScaleMatrix[sprites];

            for (var i = 0; i < sprites; i++)
            {
                sources[i] = SKRect.Create(i * 8, 0, 8, 10);
                placements[i] = SKRotationScaleMatrix.CreateTranslation(i * 9, 0);
            }

            return new DrawingContextImpl.SkiaGlyphAtlasBatch(sources, placements, null,
                SkiaOptions.DefaultStretchVerticesMinSprites);
        }
    }
}
