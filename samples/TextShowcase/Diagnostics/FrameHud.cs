using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Skia;
using SkiaSharp;

namespace TextShowcase.Diagnostics
{
    /// <summary>One render pass as the render thread saw it.</summary>
    internal sealed class FrameReading
    {
        /// <summary>Render-thread wall time from applying the batch to the end of the pass, present included.</summary>
        public double RenderMs;

        public long MaskTierDraws;
        public long TransformedTierDraws;
        public long FallbackTierDraws;
        public long AtlasDraws;
        public long BatchesDrawn;
        public long PageUploads;
        public long PageTextureUpdates;
        public long Rasterizations;

        // Process state after the pass.
        public long GlyphCacheBytes;
        public int SharedAtlasPages;
        public long SharedAtlasBytes;
        public long SkiaFontCacheBytes;
        public int SkiaFontCacheGlyphs;
    }

    /// <summary>
    /// Reads every rendered frame from the render thread the way TextStress does: the pending
    /// composition batch's <see cref="CompositionBatch.Processed"/> and
    /// <see cref="CompositionBatch.Rendered"/> tasks complete synchronously on the render thread
    /// at the start and end of the pass that draws it, so continuations that run synchronously
    /// read that thread's clocks and per-thread text counters. Both panes draw in one pass, so
    /// the time is the window's; the counters belong to one mode each (atlas and tiers to
    /// Managed, the Skia font cache to Backend).
    /// </summary>
    internal sealed class FrameHud
    {
        private readonly Compositor _compositor;
        private readonly List<double> _window = new();
        private readonly double[] _sorted = new double[Capacity];

        private const int Capacity = 90;

        public FrameHud(Compositor compositor)
        {
            _compositor = compositor;
            TextTierDiagnostics.CountTiers = true;
        }

        /// <summary>Raised on the UI thread once a frame's reading is complete.</summary>
        public event Action<FrameReading>? FrameMeasured;

        public FrameReading? Last { get; private set; }

        /// <summary>Whether tier draws are counted; the counting costs one interlocked add per managed draw.</summary>
        public static bool CountTiers
        {
            get => TextTierDiagnostics.CountTiers;
            set => TextTierDiagnostics.CountTiers = value;
        }

        /// <summary>
        /// Attaches to the batch the current UI frame will commit. Call once per frame, after the
        /// frame's changes were made.
        /// </summary>
        public void Observe()
        {
            var batch = _compositor.RequestCompositionBatchCommitAsync();
            var reading = new FrameReading();
            long start = 0;
            long mask = 0, transformed = 0, fallback = 0, atlas = 0, batches = 0, uploads = 0, updates = 0,
                rasterizations = 0;

            _ = batch.Processed.ContinueWith(_ =>
            {
                mask = Interlocked.Read(ref TextTierDiagnostics.MaskTierDraws);
                transformed = Interlocked.Read(ref TextTierDiagnostics.TransformedMaskTierDraws);
                fallback = Interlocked.Read(ref TextTierDiagnostics.BlobTierDraws);
                atlas = DrawingContextImpl.AtlasDrawsOnThread;
                batches = DrawingContextImpl.BatchesDrawnOnThread;
                uploads = DrawingContextImpl.PageImagesCreatedOnThread;
                updates = DrawingContextImpl.PageTextureUpdatesOnThread;
                rasterizations = GlyphRasterDiagnostics.GlyphRasterizationsOnThread;
                start = Stopwatch.GetTimestamp();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            _ = batch.Rendered.ContinueWith(_ =>
            {
                var end = Stopwatch.GetTimestamp();

                reading.RenderMs = start == 0 ? double.NaN : (end - start) * 1000.0 / Stopwatch.Frequency;
                reading.MaskTierDraws = Interlocked.Read(ref TextTierDiagnostics.MaskTierDraws) - mask;
                reading.TransformedTierDraws = Interlocked.Read(ref TextTierDiagnostics.TransformedMaskTierDraws) - transformed;
                reading.FallbackTierDraws = Interlocked.Read(ref TextTierDiagnostics.BlobTierDraws) - fallback;
                reading.AtlasDraws = DrawingContextImpl.AtlasDrawsOnThread - atlas;
                reading.BatchesDrawn = DrawingContextImpl.BatchesDrawnOnThread - batches;
                reading.PageUploads = DrawingContextImpl.PageImagesCreatedOnThread - uploads;
                reading.PageTextureUpdates = DrawingContextImpl.PageTextureUpdatesOnThread - updates;
                reading.Rasterizations = GlyphRasterDiagnostics.GlyphRasterizationsOnThread - rasterizations;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            // Back on the UI thread: process-wide readings, then publish.
            _ = batch.Rendered.ContinueWith(_ =>
            {
                reading.GlyphCacheBytes = GlyphCacheBudget.Shared.UsedBytes;
                reading.SharedAtlasPages = GlyphMaskAtlas.Shared.GetPages().Length;
                reading.SharedAtlasBytes = GlyphMaskAtlas.Shared.AllocatedBytes;
                reading.SkiaFontCacheBytes = SKGraphics.GetFontCacheUsed();
                reading.SkiaFontCacheGlyphs = SKGraphics.GetFontCacheCountUsed();

                if (!double.IsNaN(reading.RenderMs))
                {
                    if (_window.Count == Capacity)
                    {
                        _window.RemoveAt(0);
                    }

                    _window.Add(reading.RenderMs);
                }

                Last = reading;
                FrameMeasured?.Invoke(reading);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Median and 95th percentile render time of the last frames.</summary>
        public (double P50, double P95, int Count) Percentiles()
        {
            var count = _window.Count;

            if (count == 0)
            {
                return (double.NaN, double.NaN, 0);
            }

            _window.CopyTo(_sorted);
            Array.Sort(_sorted, 0, count);

            return (_sorted[count / 2], _sorted[Math.Min(count - 1, (int)(count * 0.95))], count);
        }

        public static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return double.NaN;
            }

            var copy = values.ToArray();
            Array.Sort(copy);

            return copy[copy.Length / 2];
        }
    }
}
