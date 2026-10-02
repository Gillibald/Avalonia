using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        private const int DefaultMaxPendingBatches = 32;

        private static int s_maxPendingBatches = DefaultMaxPendingBatches;

        /// <summary>
        /// Pending batches at once, each of one page in one colour or several opaque ones. Every
        /// typeface has its own atlas, so a list whose rows mix a dozen or two typefaces keeps
        /// that many batches pending; past this many, all of them are drawn rather than searched.
        /// A run joining a batch compares its key with every pending batch and its bounds with
        /// each one's union, so this bounds that work per run alongside
        /// <see cref="MaxPendingRuns"/>.
        /// Profiling tools change it to measure how the draw count depends on it; a context takes
        /// the value when it begins its first batch.
        /// </summary>
        internal static int MaxPendingBatches
        {
            get => s_maxPendingBatches;
            set => s_maxPendingBatches = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one batch must be pending.");
        }

        // Runs pending in the other batches before a run joins one: the run is checked against
        // every run of the others, so this bounds that work per run. A batch's own runs are not
        // tested against it, so text of one page and colour stays one batch however many runs.
        private const int MaxPendingRuns = 128;

        // Contexts are made per frame, so pending batches and their run lists come from a
        // per-thread pool instead of each context growing its own; a layer drawn while its
        // parent holds batches rents others.
        [ThreadStatic]
        private static Stack<PendingGlyphBatch>? t_pendingBatches;

        private PendingGlyphBatch[]? _pendingBatches;
        private int _pendingBatchCount;
        private int _pendingRunCount;

        /// <summary>
        /// Whether glyph atlas draws on this GPU context are collected across glyph runs and
        /// drawn with one call per page, for opaque colours, or per page and colour. On by
        /// default; tests turn it off to draw every run on its own and compare.
        /// </summary>
        internal bool BatchesGlyphAtlasDraws { get; set; } = true;

        /// <summary>
        /// Appends a run's atlas batch drawn 1:1 at a whole-pixel offset to the pending batch of
        /// its page and colour. Returns <c>false</c> when the draw cannot be batched.
        /// </summary>
        /// <remarks>
        /// Runs of one page in opaque colours share a batch, each sprite modulated by its run's
        /// colour: a page holds coverage corrected for one luminance bucket, so every colour
        /// sampling it is of that bucket. A translucent colour has a batch of its own, since
        /// Skia rounds a translucent per-sprite colour differently from a paint colour.
        /// <para>
        /// Several batches can be pending, one per page and colour, as long as no run of one
        /// overlaps a run of another: then the batches can be drawn in any order and every pixel
        /// still sees its runs in drawing order, so lines that alternate typefaces or colours
        /// draw in one call per page and colour. A run that overlaps a run of another pending
        /// batch draws every pending batch first, in the order they were begun.
        /// </para>
        /// <para>
        /// The page image is made when a batch is drawn, not here: runs appended later may
        /// still add glyphs to the page, and the image of its latest version shows every
        /// sprite appended before. That also uploads a page once per batch instead of once
        /// per run that wrote to it.
        /// </para>
        /// <para>
        /// A batch of several runs holds at most <see cref="MaxSpritesPerAtlasDraw"/> sprites, so
        /// a context that is never flushed, kept across frames say, draws its batches as it goes
        /// instead of piling up runs without bound. A single run of more sprites is a batch of
        /// its own and draws in several calls.
        /// </para>
        /// </remarks>
        private bool TryAppendToGlyphBatch(GlyphAtlasPage page, SkiaGlyphAtlasBatch backend, in Matrix transform,
            SKColor color)
        {
            // Sprites are placed in device pixels; a hidden DPI transform would scale the
            // offsets differently from the run's own placement.
            if (!BatchesGlyphAtlasDraws || _grContext is null || _postTransform.HasValue ||
                transform.M11 != 1 || transform.M12 != 0 || transform.M21 != 0 || transform.M22 != 1 ||
                transform.M31 != Math.Round(transform.M31) || transform.M32 != Math.Round(transform.M32) ||
                Math.Abs(transform.M31) > 1 << 24 || Math.Abs(transform.M32) > 1 << 24)
            {
                return false;
            }

            FlushLcdBatch(GlyphBatchFlushReason.OtherTextPath);

            var x = (int)transform.M31;
            var y = (int)transform.M32;
            var bounds = backend.Bounds;

            bounds.Offset(x, y);
            CountClippedRun(bounds);

            if (!TryTrimToClips(ref backend, x, y, ref bounds))
            {
                return true;
            }

            var target = FindPendingBatch(page, color);

            // A full batch draws on its own: it overlaps no other pending batch, so drawing it
            // ahead of them keeps every pixel's order.
            if (target is not null && target.SpriteCount + backend.Sources.Length > MaxSpritesPerAtlasDraw)
            {
                FlushPendingBatch(target, GlyphBatchFlushReason.SpriteCap);
                target = null;
            }

            if (_pendingRunCount - (target?.RunCount ?? 0) >= MaxPendingRuns)
            {
                FlushAtlasBatch(GlyphBatchFlushReason.RunLimit);
                target = null;
            }
            else if (FindOverlappingPendingBatch(bounds, target) is { } overlapped)
            {
                FlushAtlasBatch(overlapped.Page == page
                    ? GlyphBatchFlushReason.ColorChange
                    : GlyphBatchFlushReason.PageChange);
                target = null;
            }

            if (target is null)
            {
                if (_pendingBatches is { } slots && _pendingBatchCount == slots.Length)
                {
                    FlushAtlasBatch(GlyphBatchFlushReason.SlotPressure);
                }

                target = (t_pendingBatches is { Count: > 0 } pool ? pool.Pop() : new PendingGlyphBatch());
                target.Page = page;
                target.Color = color;

                var pending = _pendingBatches ??= new PendingGlyphBatch[MaxPendingBatches];

                pending[_pendingBatchCount++] = target;
            }

            target.Add(new BatchedRun(backend, x, y, color), bounds);
            _pendingRunCount++;

            return true;
        }

        private PendingGlyphBatch? FindPendingBatch(GlyphAtlasPage page, SKColor color)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                var batch = _pendingBatches![i];

                if (batch.Page == page && (batch.Color == color || (batch.Color.Alpha == 255 && color.Alpha == 255)))
                {
                    return batch;
                }
            }

            return null;
        }

        /// <summary>
        /// The first pending batch other than <paramref name="target"/> that a run covering
        /// <paramref name="bounds"/> overlaps.
        /// </summary>
        private PendingGlyphBatch? FindOverlappingPendingBatch(SKRect bounds, PendingGlyphBatch? target)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                var batch = _pendingBatches![i];

                if (batch != target && batch.Overlaps(bounds))
                {
                    return batch;
                }
            }

            return null;
        }

        /// <summary>
        /// Draws the pending glyph batches, grayscale or subpixel, and applies the deferred clips
        /// to the canvas. Every canvas operation calls this first, as does the end of the drawing
        /// session, so pending sprites never change their place in the draw order or the clip and
        /// layer they were collected under, and the operation draws under every clip pushed. A
        /// caller reading the surface back while this context is still drawing calls it too. It
        /// also drops the kept direct-write target, since the operation that follows may change
        /// the clip or move the surface's pixels.
        /// </summary>
        internal void FlushGlyphBatch() => FlushGlyphBatch(GlyphBatchFlushReason.Other);

        /// <inheritdoc cref="FlushGlyphBatch()"/>
        /// <param name="reason">Why the batches are drawn now, for the flush counters.</param>
        internal void FlushGlyphBatch(GlyphBatchFlushReason reason)
        {
            ForgetBlitTarget();
            FlushAtlasBatch(reason);
            FlushLcdBatch(reason);
            ApplyDeferredClips();
        }

        /// <summary>Draws every pending grayscale batch, in the order they were begun.</summary>
        private void FlushAtlasBatch(GlyphBatchFlushReason reason)
        {
            if (_pendingBatchCount > 0)
            {
                CountFlush(reason);
            }

            for (var i = 0; i < _pendingBatchCount; i++)
            {
                DrawPendingBatch(_pendingBatches![i], reason);
                _pendingBatches[i] = null!;
            }

            _pendingBatchCount = 0;
            _pendingRunCount = 0;
        }

        /// <summary>Draws one pending batch ahead of the others and takes it off the pending list.</summary>
        private void FlushPendingBatch(PendingGlyphBatch batch, GlyphBatchFlushReason reason)
        {
            var pending = _pendingBatches!;
            var index = Array.IndexOf(pending, batch, 0, _pendingBatchCount);

            _pendingRunCount -= batch.RunCount;
            CountFlush(reason);
            DrawPendingBatch(batch, reason);

            Array.Copy(pending, index + 1, pending, index, _pendingBatchCount - index - 1);
            pending[--_pendingBatchCount] = null!;
        }

        /// <summary>Draws a pending grayscale batch and returns it to the pool.</summary>
        /// <remarks>
        /// Skia turns an atlas draw's sprites into quads on the CPU every time it is called. A
        /// batch that repeats the previous batch begun by the same run, the same runs at the
        /// same places relative to the first, draws from vertices built once instead, translated
        /// to where the first run is now: static and scrolled text submits no new geometry. The
        /// vertices are made the first time a batch repeats, so text that changes every frame
        /// never pays for building them. The vertices sample the page through its image shader
        /// at the texel centres the atlas draw samples, and modulate the paint colour by the
        /// coverage alike, so both draws produce the same pixels.
        /// <para>
        /// A batch of several opaque colours draws with a white paint and modulates each sprite,
        /// or each vertex, by its run's colour instead. For opaque colours Skia produces the
        /// pixels of a paint colour that way, and for any colour through vertices; a
        /// translucent per-sprite atlas colour is off by one step in places, which is why
        /// translucent colours never share a batch.
        /// </para>
        /// </remarks>
        private void DrawPendingBatch(PendingGlyphBatch batch, GlyphBatchFlushReason reason)
        {
            CountBatchDrawn(reason, batch.RunCount);

            var page = batch.Page!;
            var runs = batch.Runs;
            var count = batch.RunCount;
            var spriteCount = batch.SpriteCount;
            var first = runs[0];

            // A page dropped from the atlas after its sprites were appended still holds their
            // coverage; its image is made for this draw alone, since the page keeps none.
            var transient = page.IsEvicted ? new GlyphPageImage(CreatePageImage(page)) : null;
            var image = transient ?? GetPageImage(page);
            var paint = SKPaintCache.Shared.Get();
            var oldTransform = Transform;
            var draws = 1;
            var colored = batch.HasMixedColors;

            paint.Color = colored ? SKColors.White : batch.Color;

            if (first.Backend.TryGetBatchVertices(runs, count, spriteCount, colored, out var vertices, out var built))
            {
                paint.Shader = image.Shader;
                Transform = Matrix.CreateTranslation(first.X, first.Y);

                foreach (var part in vertices)
                {
                    Canvas.DrawVertices(part, SKBlendMode.Modulate, paint);
                }

                draws = vertices.Length;

                if (built)
                {
                    t_atlasGeometry += spriteCount;
                }
            }
            else if (count == 1)
            {
                // A single run draws its own arrays under its placement, exactly as an unbatched
                // draw would.
                Transform = Matrix.CreateTranslation(first.X, first.Y);
                draws = DrawAtlasSprites(image.Image, first.Backend.Sources, first.Backend.Placements, s_nearest,
                    paint);
                t_atlasGeometry += spriteCount;
            }
            else
            {
                // DrawAtlas takes the sprite count from the array lengths. Several runs are only
                // batched together within one draw's sprites.
                var (sources, placements) = GetTransientSpriteArrays(spriteCount);
                var colors = colored ? GetTransientSpriteColors(spriteCount) : null;
                var offset = 0;

                for (var i = 0; i < count; i++)
                {
                    var run = runs[i];
                    var runSources = run.Backend.Sources;
                    var runPlacements = run.Backend.Placements;

                    Array.Copy(runSources, 0, sources, offset, runSources.Length);

                    if (colors is not null)
                    {
                        colors.AsSpan(offset, runSources.Length).Fill(run.Color);
                    }

                    for (var j = 0; j < runPlacements.Length; j++)
                    {
                        var placement = runPlacements[j];

                        placements[offset + j] = SKRotationScaleMatrix.CreateTranslation(placement.TX + run.X,
                            placement.TY + run.Y);
                    }

                    offset += runSources.Length;
                }

                Transform = Matrix.Identity;

                if (colors is not null)
                {
                    Canvas.DrawAtlas(image.Image, sources, placements, colors, SKBlendMode.Modulate, s_nearest, paint);
                }
                else
                {
                    Canvas.DrawAtlas(image.Image, sources, placements, s_nearest, paint);
                }

                t_atlasGeometry += spriteCount;
            }

            Transform = oldTransform;
            SKPaintCache.Shared.ReturnReset(paint);
            transient?.Dispose();
            t_atlasDraws += draws;

            batch.Clear();
            (t_pendingBatches ??= new Stack<PendingGlyphBatch>()).Push(batch);
        }

        private static readonly int s_flushReasonCount = Enum.GetValues<GlyphBatchFlushReason>().Length;

        // Per flush reason: the batches drawn, and the flushes that drew at least one.
        [ThreadStatic]
        private static long[]? t_batchesFlushed;

        [ThreadStatic]
        private static long[]? t_flushes;

        [ThreadStatic]
        private static long t_batchedRuns;

        [ThreadStatic]
        private static long t_batchesDrawn;

        [ThreadStatic]
        private static int t_maxRunsPerBatch;

        /// <summary>
        /// The pending glyph batches, grayscale or subpixel, drawn on this thread because of
        /// <paramref name="reason"/>; for profiling tools and tests.
        /// </summary>
        internal static long GetBatchesFlushedOnThread(GlyphBatchFlushReason reason) =>
            t_batchesFlushed?[(int)reason] ?? 0;

        /// <summary>
        /// The flushes on this thread that drew at least one pending glyph batch because of
        /// <paramref name="reason"/>; for profiling tools and tests.
        /// </summary>
        internal static long GetFlushesOnThread(GlyphBatchFlushReason reason) => t_flushes?[(int)reason] ?? 0;

        /// <summary>The runs, or subpixel entries, in the pending glyph batches drawn on this thread.</summary>
        internal static long BatchedRunsOnThread => t_batchedRuns;

        /// <summary>The pending glyph batches drawn on this thread, each one atlas draw or more.</summary>
        internal static long BatchesDrawnOnThread => t_batchesDrawn;

        /// <summary>
        /// The most runs one pending glyph batch drawn on this thread held since the last call,
        /// starting the next span at zero.
        /// </summary>
        internal static int TakeMaxRunsPerBatchOnThread()
        {
            var max = t_maxRunsPerBatch;

            t_maxRunsPerBatch = 0;

            return max;
        }

        private static void CountFlush(GlyphBatchFlushReason reason) =>
            (t_flushes ??= new long[s_flushReasonCount])[(int)reason]++;

        private static void CountBatchDrawn(GlyphBatchFlushReason reason, int runs)
        {
            (t_batchesFlushed ??= new long[s_flushReasonCount])[(int)reason]++;
            t_batchedRuns += runs;
            t_batchesDrawn++;

            if (runs > t_maxRunsPerBatch)
            {
                t_maxRunsPerBatch = runs;
            }
        }

        /// <summary>
        /// The runs of one page waiting to be drawn together, in one colour or in several opaque
        /// ones, with each run's device bounds for the overlap test against other pending batches.
        /// </summary>
        private sealed class PendingGlyphBatch
        {
            public GlyphAtlasPage? Page;

            /// <summary>The colour of the first run; the batch's colour unless it has several.</summary>
            public SKColor Color;

            /// <summary>Whether a run's colour differs from <see cref="Color"/>.</summary>
            public bool HasMixedColors;

            public BatchedRun[] Runs = new BatchedRun[32];
            public int RunCount;
            public int SpriteCount;

            private SKRect[] _bounds = new SKRect[32];
            private SKRect _union = SKRect.Empty;

            /// <summary>The device rectangle the runs cover.</summary>
            public SKRect Bounds => _union;

            public void Add(in BatchedRun run, SKRect bounds)
            {
                if (RunCount == Runs.Length)
                {
                    Array.Resize(ref Runs, RunCount * 2);
                    Array.Resize(ref _bounds, RunCount * 2);
                }

                Runs[RunCount] = run;
                _bounds[RunCount] = bounds;
                HasMixedColors |= run.Color != Color;
                _union = RunCount == 0 ? bounds : SKRect.Union(_union, bounds);
                RunCount++;
                SpriteCount += run.Backend.Sources.Length;
            }

            /// <summary>Whether <paramref name="bounds"/> overlaps one of the runs, edges excluded.</summary>
            public bool Overlaps(SKRect bounds)
            {
                if (!Intersects(_union, bounds))
                {
                    return false;
                }

                for (var i = 0; i < RunCount; i++)
                {
                    if (Intersects(_bounds[i], bounds))
                    {
                        return true;
                    }
                }

                return false;
            }

            public void Clear()
            {
                // The pooled lists must not keep the runs' arrays or the page alive.
                Array.Clear(Runs, 0, RunCount);
                RunCount = 0;
                SpriteCount = 0;
                HasMixedColors = false;
                Page = null;
                _union = SKRect.Empty;
            }

            private static bool Intersects(SKRect a, SKRect b)
                => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
        }

        /// <summary>
        /// A run's atlas sprites in a pending batch, placed at a device pixel offset and drawn in
        /// a colour.
        /// </summary>
        internal readonly record struct BatchedRun(SkiaGlyphAtlasBatch Backend, int X, int Y, SKColor Color);
    }
}
