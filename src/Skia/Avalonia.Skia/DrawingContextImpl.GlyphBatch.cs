using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Pending batches at once, each of one page and colour. Text alternating a few typefaces
        // or colours fills a few; more than this is flushed rather than searched.
        private const int MaxPendingBatches = 8;

        // Runs pending across all batches before they are drawn: a run joining a batch is
        // checked against every run of the others, so this bounds that work per run.
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
        /// drawn with one call per page and colour. On by default; tests turn it off to draw
        /// every run on its own and compare.
        /// </summary>
        internal bool BatchesGlyphAtlasDraws { get; set; } = true;

        /// <summary>
        /// Appends a run's atlas batch drawn 1:1 at a whole-pixel offset to the pending batch of
        /// its page and colour. Returns <c>false</c> when the draw cannot be batched.
        /// </summary>
        /// <remarks>
        /// Several batches can be pending, one per page and colour, as long as no run of one
        /// overlaps a run of another: then the batches can be drawn in any order and every pixel
        /// still sees its runs in drawing order, so lines that alternate typefaces or colours
        /// draw in one call per page and colour. A run that overlaps a run of another pending
        /// batch draws every pending batch first, in the order they were begun.
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

            FlushLcdBatch();

            var x = (int)transform.M31;
            var y = (int)transform.M32;
            var bounds = backend.Bounds;

            bounds.Offset(x, y);

            var target = FindPendingBatch(page, color);

            // A full batch draws on its own: it overlaps no other pending batch, so drawing it
            // ahead of them keeps every pixel's order.
            if (target is not null && target.SpriteCount + backend.Sources.Length > MaxSpritesPerAtlasDraw)
            {
                FlushPendingBatch(target);
                target = null;
            }

            if (_pendingRunCount >= MaxPendingRuns || OverlapsOtherPendingBatch(bounds, target))
            {
                FlushAtlasBatch();
                target = null;
            }

            if (target is null)
            {
                if (_pendingBatchCount == MaxPendingBatches)
                {
                    FlushAtlasBatch();
                }

                target = (t_pendingBatches is { Count: > 0 } pool ? pool.Pop() : new PendingGlyphBatch());
                target.Page = page;
                target.Color = color;

                var pending = _pendingBatches ??= new PendingGlyphBatch[MaxPendingBatches];

                pending[_pendingBatchCount++] = target;
            }

            target.Add(new BatchedRun(backend, x, y), bounds);
            _pendingRunCount++;

            return true;
        }

        private PendingGlyphBatch? FindPendingBatch(GlyphAtlasPage page, SKColor color)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                var batch = _pendingBatches![i];

                if (batch.Page == page && batch.Color == color)
                {
                    return batch;
                }
            }

            return null;
        }

        private bool OverlapsOtherPendingBatch(SKRect bounds, PendingGlyphBatch? target)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                var batch = _pendingBatches![i];

                if (batch != target && batch.Overlaps(bounds))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Draws the pending glyph batches, grayscale or subpixel. Every canvas operation calls
        /// this first, as does the end of the drawing session, so pending sprites never change
        /// their place in the draw order or the clip and layer they were collected under. A
        /// caller reading the surface back while this context is still drawing calls it too.
        /// </summary>
        internal void FlushGlyphBatch()
        {
            FlushAtlasBatch();
            FlushLcdBatch();
        }

        /// <summary>Draws every pending grayscale batch, in the order they were begun.</summary>
        private void FlushAtlasBatch()
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                DrawPendingBatch(_pendingBatches![i]);
                _pendingBatches[i] = null!;
            }

            _pendingBatchCount = 0;
            _pendingRunCount = 0;
        }

        /// <summary>Draws one pending batch ahead of the others and takes it off the pending list.</summary>
        private void FlushPendingBatch(PendingGlyphBatch batch)
        {
            var pending = _pendingBatches!;
            var index = Array.IndexOf(pending, batch, 0, _pendingBatchCount);

            _pendingRunCount -= batch.RunCount;
            DrawPendingBatch(batch);

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
        /// </remarks>
        private void DrawPendingBatch(PendingGlyphBatch batch)
        {
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

            paint.Color = batch.Color;

            if (first.Backend.TryGetBatchVertices(runs, count, spriteCount, out var vertices, out var built))
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
                var offset = 0;

                for (var i = 0; i < count; i++)
                {
                    var run = runs[i];
                    var runSources = run.Backend.Sources;
                    var runPlacements = run.Backend.Placements;

                    Array.Copy(runSources, 0, sources, offset, runSources.Length);

                    for (var j = 0; j < runPlacements.Length; j++)
                    {
                        var placement = runPlacements[j];

                        placements[offset + j] = SKRotationScaleMatrix.CreateTranslation(placement.TX + run.X,
                            placement.TY + run.Y);
                    }

                    offset += runSources.Length;
                }

                Transform = Matrix.Identity;
                Canvas.DrawAtlas(image.Image, sources, placements, s_nearest, paint);
                t_atlasGeometry += spriteCount;
            }

            Transform = oldTransform;
            SKPaintCache.Shared.ReturnReset(paint);
            transient?.Dispose();
            t_atlasDraws += draws;

            batch.Clear();
            (t_pendingBatches ??= new Stack<PendingGlyphBatch>()).Push(batch);
        }

        /// <summary>
        /// The runs of one page and colour waiting to be drawn together, with each run's device
        /// bounds for the overlap test against other pending batches.
        /// </summary>
        private sealed class PendingGlyphBatch
        {
            public GlyphAtlasPage? Page;
            public SKColor Color;
            public BatchedRun[] Runs = new BatchedRun[32];
            public int RunCount;
            public int SpriteCount;

            private SKRect[] _bounds = new SKRect[32];
            private SKRect _union = SKRect.Empty;

            public void Add(in BatchedRun run, SKRect bounds)
            {
                if (RunCount == Runs.Length)
                {
                    Array.Resize(ref Runs, RunCount * 2);
                    Array.Resize(ref _bounds, RunCount * 2);
                }

                Runs[RunCount] = run;
                _bounds[RunCount] = bounds;
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
                Page = null;
                _union = SKRect.Empty;
            }

            private static bool Intersects(SKRect a, SKRect b)
                => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
        }

        /// <summary>A run's atlas sprites in a pending batch, placed at a device pixel offset.</summary>
        internal readonly record struct BatchedRun(SkiaGlyphAtlasBatch Backend, int X, int Y);
    }
}
