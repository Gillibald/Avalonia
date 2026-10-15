using System;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Skia
{
    /// <summary>
    /// Running totals of the glyph atlas, batching and clipping work the Skia backend did on one
    /// thread for one application, kept in the thread's <see cref="TextRasterCounters.BackendCounters"/>;
    /// for profiling tools and tests. Like the managed rasterization counters they only grow, are
    /// written by their thread alone, and nothing reads them back to decide anything.
    /// </summary>
    internal sealed class SkiaTextCounters
    {
        private static readonly int s_flushReasonCount = Enum.GetValues<GlyphBatchFlushReason>().Length;
        private static readonly int s_clipKindCount = Enum.GetValues<GlyphRunClipKind>().Length;

        // Per flush reason: the batches drawn, and the flushes that drew at least one.
        private readonly long[] _batchesFlushed = new long[s_flushReasonCount];
        private readonly long[] _flushes = new long[s_flushReasonCount];

        // Per clip kind: the runs crossing the clip's edge, then those inside it.
        private readonly long[] _clippedRuns = new long[s_clipKindCount * 2];

        /// <summary>
        /// The atlas page images made, each holding the whole page: a page texture, or an image
        /// wrapping the page's array, which a GPU context uploads whole.
        /// </summary>
        public int PageImagesCreated;

        /// <summary>The atlas page images made that replaced the image or texture of their page.</summary>
        public int PageImagesReplaced;

        /// <summary>
        /// The atlas page bytes handed to the GPU: whole pages for new page images, written
        /// rectangles for page texture updates.
        /// </summary>
        public long PageImageBytes;

        /// <summary>The page texture updates, each uploading what the atlas wrote to the page since the last.</summary>
        public int PageTextureUpdates;

        /// <summary>
        /// The rectangles the page texture updates uploaded, several per update where the atlas
        /// wrote to several places of the page.
        /// </summary>
        public int PageTextureRegions;

        /// <summary>The atlas draw calls issued.</summary>
        public int AtlasDraws;

        /// <summary>
        /// The glyph atlas sprites whose geometry was handed to Skia; sprites drawn from geometry
        /// kept from an earlier frame do not count.
        /// </summary>
        public int AtlasGeometrySubmitted;

        /// <summary>The runs, or subpixel entries, in the pending glyph batches drawn.</summary>
        public long BatchedRuns;

        /// <summary>The pending glyph batches drawn, each one atlas draw or more.</summary>
        public long BatchesDrawn;

        /// <summary>The batched glyph runs drawn under no clip, or inside every clip pushed.</summary>
        public long RunsInsideEveryClip;

        /// <summary>The glyph runs drawn from upright run masks, counted with <see cref="SkiaOptions.CountTextTiers"/>.</summary>
        public long MaskTierDraws;

        /// <summary>The glyph runs drawn from transformed run masks or sprites, counted with <see cref="SkiaOptions.CountTextTiers"/>.</summary>
        public long TransformedMaskTierDraws;

        /// <summary>The glyph runs drawn as native text blobs, counted with <see cref="SkiaOptions.CountTextTiers"/>.</summary>
        public long BlobTierDraws;

        private int _maxRunsPerBatch;

        /// <summary>The counters of <paramref name="state"/>'s thread, made on first use.</summary>
        public static SkiaTextCounters Of(TextRasterThreadState state)
            => state.Counters.BackendCounters as SkiaTextCounters ??
               (SkiaTextCounters)(state.Counters.BackendCounters = new SkiaTextCounters());

        /// <summary>
        /// The sum of <paramref name="counter"/> over the threads of <paramref name="services"/>' application
        /// that drew through Skia, for readers on another thread than the one drawing (see
        /// <see cref="GlyphRasterServices.GetThreadStates"/>).
        /// </summary>
        public static long Sum(GlyphRasterServices services, Func<SkiaTextCounters, long> counter)
        {
            long sum = 0;

            foreach (var state in services.GetThreadStates())
            {
                if (state.Counters.BackendCounters is SkiaTextCounters counters)
                {
                    sum += counter(counters);
                }
            }

            return sum;
        }

        /// <summary>The pending glyph batches, grayscale or subpixel, drawn because of <paramref name="reason"/>.</summary>
        public long GetBatchesFlushed(GlyphBatchFlushReason reason) => _batchesFlushed[(int)reason];

        /// <summary>The flushes that drew at least one pending glyph batch because of <paramref name="reason"/>.</summary>
        public long GetFlushes(GlyphBatchFlushReason reason) => _flushes[(int)reason];

        /// <summary>
        /// The batched glyph runs whose innermost clip is of <paramref name="kind"/>, and whose
        /// device bounds lie inside that clip (<paramref name="inside"/>) or cross its edge. For a
        /// rounded rectangle, a geometry or a transformed clip, inside means inside its device bounds.
        /// </summary>
        public long GetClippedRuns(GlyphRunClipKind kind, bool inside) => _clippedRuns[(int)kind * 2 + (inside ? 1 : 0)];

        /// <summary>The most runs one pending glyph batch held since the last call, starting the next span at zero.</summary>
        public int TakeMaxRunsPerBatch()
        {
            var max = _maxRunsPerBatch;

            _maxRunsPerBatch = 0;

            return max;
        }

        internal void CountFlush(GlyphBatchFlushReason reason) => _flushes[(int)reason]++;

        internal void CountBatchDrawn(GlyphBatchFlushReason reason, int runs)
        {
            _batchesFlushed[(int)reason]++;
            BatchedRuns += runs;
            BatchesDrawn++;

            if (runs > _maxRunsPerBatch)
            {
                _maxRunsPerBatch = runs;
            }
        }

        internal void CountClippedRun(GlyphRunClipKind kind, bool inside) => _clippedRuns[(int)kind * 2 + (inside ? 1 : 0)]++;
    }
}
