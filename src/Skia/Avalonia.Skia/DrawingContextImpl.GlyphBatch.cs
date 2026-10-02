using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Contexts are made per frame, so the batch's run list comes from a per-thread pool
        // instead of each context growing its own; a layer drawn while its parent holds a batch
        // rents a second one.
        [ThreadStatic]
        private static Stack<BatchedRun[]>? t_batchRuns;

        private BatchedRun[]? _batchRuns;
        private int _batchRunCount;
        private int _batchCount;
        private GlyphAtlasPage? _batchPage;
        private SKColor _batchColor;

        /// <summary>
        /// Whether glyph atlas draws on this GPU context are collected across glyph runs and
        /// drawn with one call per page and colour. On by default; tests turn it off to draw
        /// every run on its own and compare.
        /// </summary>
        internal bool BatchesGlyphAtlasDraws { get; set; } = true;

        /// <summary>
        /// Appends a run's atlas batch drawn 1:1 at a whole-pixel offset to the pending glyph
        /// batch, flushing first when the batch samples another page, draws another colour, or
        /// would outgrow one atlas draw. Returns <c>false</c> when the draw cannot be batched.
        /// </summary>
        /// <remarks>
        /// The page image is made when the batch is drawn, not here: runs appended later may
        /// still add glyphs to the page, and the image of its latest version shows every
        /// sprite appended before. That also uploads a page once per batch instead of once
        /// per run that wrote to it.
        /// <para>
        /// A batch of several runs holds at most <see cref="MaxSpritesPerAtlasDraw"/> sprites, so
        /// a context that is never flushed, kept across frames say, draws its batch as it goes
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

            if (_batchRunCount > 0 && (page != _batchPage || color != _batchColor ||
                                       _batchCount + backend.Sources.Length > MaxSpritesPerAtlasDraw))
            {
                FlushAtlasBatch();
            }

            var runs = _batchRuns ??= t_batchRuns is { Count: > 0 } pool ? pool.Pop() : new BatchedRun[32];

            if (_batchRunCount == runs.Length)
            {
                Array.Resize(ref _batchRuns, runs.Length * 2);
                runs = _batchRuns;
            }

            if (_batchRunCount == 0)
            {
                _batchPage = page;
                _batchColor = color;
            }

            runs[_batchRunCount++] = new BatchedRun(backend, (int)transform.M31, (int)transform.M32);
            _batchCount += backend.Sources.Length;

            return true;
        }

        /// <summary>
        /// Draws the pending glyph batch, grayscale or subpixel; at most one is pending. Every
        /// canvas operation calls this first, as does the end of the drawing session, so pending
        /// sprites never change their place in the draw order or the clip and layer they were
        /// collected under. A caller reading the surface back while this context is still drawing
        /// calls it too. It also drops the kept direct-write target, since the operation that
        /// follows may change the clip or move the surface's pixels.
        /// </summary>
        internal void FlushGlyphBatch()
        {
            ForgetBlitTarget();
            FlushAtlasBatch();
            FlushLcdBatch();
        }

        /// <summary>Draws the pending grayscale batch.</summary>
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
        private void FlushAtlasBatch()
        {
            if (_batchRunCount == 0)
            {
                return;
            }

            var page = _batchPage!;
            var runs = _batchRuns!;
            var count = _batchRunCount;
            var first = runs[0];

            // A page dropped from the atlas after its sprites were appended still holds their
            // coverage; its image is made for this draw alone, since the page keeps none.
            var transient = page.IsEvicted ? new GlyphPageImage(CreatePageImage(page)) : null;
            var image = transient ?? GetPageImage(page);
            var paint = SKPaintCache.Shared.Get();
            var oldTransform = Transform;
            var draws = 1;

            paint.Color = _batchColor;

            if (first.Backend.TryGetBatchVertices(runs, count, _batchCount, out var vertices, out var built))
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
                    t_atlasGeometry += _batchCount;
                }
            }
            else if (count == 1)
            {
                // A single run draws its own arrays under its placement, exactly as an unbatched
                // draw would.
                Transform = Matrix.CreateTranslation(first.X, first.Y);
                draws = DrawAtlasSprites(image.Image, first.Backend.Sources, first.Backend.Placements, s_nearest,
                    paint);
                t_atlasGeometry += _batchCount;
            }
            else
            {
                // DrawAtlas takes the sprite count from the array lengths. Several runs are only
                // batched together within one draw's sprites.
                var (sources, placements) = GetTransientSpriteArrays(_batchCount);
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
                t_atlasGeometry += _batchCount;
            }

            Transform = oldTransform;
            SKPaintCache.Shared.ReturnReset(paint);
            transient?.Dispose();
            t_atlasDraws += draws;

            // The pooled list must not keep the runs' arrays alive.
            Array.Clear(runs, 0, count);
            (t_batchRuns ??= new Stack<BatchedRun[]>()).Push(runs);
            _batchRuns = null;
            _batchRunCount = 0;
            _batchCount = 0;
            _batchPage = null;
        }

        /// <summary>A run's atlas sprites in a pending batch, placed at a device pixel offset.</summary>
        internal readonly record struct BatchedRun(SkiaGlyphAtlasBatch Backend, int X, int Y);
    }
}
