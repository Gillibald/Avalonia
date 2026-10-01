using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Contexts are made per frame, so the merged sprite arrays come from a per-thread pool
        // instead of each context growing its own; a layer drawn while its parent holds a
        // batch rents a second set.
        [ThreadStatic]
        private static Stack<GlyphBatchArrays>? t_batchArrays;

        private GlyphBatchArrays? _batchArrays;
        private int _batchCount;
        private GlyphAtlasPage? _batchPage;
        private SKColor _batchColor;

        // While the batch holds a single run's sprites they stay in that run's own arrays, drawn
        // under its placement exactly as an unbatched draw would; a second run copies them out.
        private SkiaGlyphAtlasBatch? _batchSingle;
        private int _batchSingleX;
        private int _batchSingleY;

        /// <summary>
        /// Whether glyph atlas draws on this GPU context are collected across glyph runs and
        /// drawn with one call per page and colour. On by default; tests turn it off to draw
        /// every run on its own and compare.
        /// </summary>
        internal bool BatchesGlyphAtlasDraws { get; set; } = true;

        /// <summary>
        /// Appends a run's atlas batch drawn 1:1 at a whole-pixel offset to the pending glyph
        /// batch, flushing first when the batch samples another page or draws another colour.
        /// Returns <c>false</c> when the draw cannot be batched.
        /// </summary>
        /// <remarks>
        /// The page image is made when the batch is drawn, not here: runs appended later may
        /// still add glyphs to the page, and the image of its latest version shows every
        /// sprite appended before. That also uploads a page once per batch instead of once
        /// per run that wrote to it.
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

            if (_batchCount > 0 && (page != _batchPage || color != _batchColor))
            {
                FlushGlyphBatch();
            }

            var x = (int)transform.M31;
            var y = (int)transform.M32;
            var count = backend.Sources.Length;

            if (_batchCount == 0)
            {
                _batchPage = page;
                _batchColor = color;
                _batchSingle = backend;
                _batchSingleX = x;
                _batchSingleY = y;
                _batchCount = count;

                return true;
            }

            if (_batchSingle is { } single)
            {
                _batchSingle = null;
                _batchCount = 0;
                CopyToGlyphBatch(single, _batchSingleX, _batchSingleY);
            }

            CopyToGlyphBatch(backend, x, y);

            return true;
        }

        private void CopyToGlyphBatch(SkiaGlyphAtlasBatch backend, int x, int y)
        {
            var arrays = _batchArrays ??= t_batchArrays is { Count: > 0 } pool ? pool.Pop() : new GlyphBatchArrays();
            var count = backend.Sources.Length;
            var required = _batchCount + count;

            if (required > arrays.Sources.Length)
            {
                var capacity = Math.Max(required, arrays.Sources.Length * 2);

                Array.Resize(ref arrays.Sources, capacity);
                Array.Resize(ref arrays.Placements, capacity);
            }

            Array.Copy(backend.Sources, 0, arrays.Sources, _batchCount, count);

            var placements = backend.Placements;
            var target = arrays.Placements.AsSpan(_batchCount, count);

            for (var i = 0; i < placements.Length; i++)
            {
                var placement = placements[i];

                target[i] = SKRotationScaleMatrix.CreateTranslation(placement.TX + x, placement.TY + y);
            }

            _batchCount = required;
        }

        /// <summary>
        /// Draws the pending glyph batch. Every canvas operation calls this first, as does the end
        /// of the drawing session, so pending sprites never change their place in the draw order
        /// or the clip and layer they were collected under. A caller reading the surface back
        /// while this context is still drawing calls it too.
        /// </summary>
        internal void FlushGlyphBatch()
        {
            if (_batchCount == 0)
            {
                return;
            }

            var page = _batchPage!;

            // A page dropped from the atlas after its sprites were appended still holds their
            // coverage; its image is made for this draw alone, since the page keeps none.
            var transient = page.IsEvicted ? CreatePageImage(page) : null;
            var image = transient ?? GetPageImage(page);
            var paint = SKPaintCache.Shared.Get();
            var oldTransform = Transform;

            paint.Color = _batchColor;

            if (_batchSingle is { } single)
            {
                Transform = Matrix.CreateTranslation(_batchSingleX, _batchSingleY);
                Canvas.DrawAtlas(image, single.Sources, single.Placements, s_nearest, paint);
            }
            else
            {
                // DrawAtlas takes the sprite count from the array lengths.
                var arrays = _batchArrays!;
                var (sources, placements) = GetTransientSpriteArrays(_batchCount);

                Array.Copy(arrays.Sources, sources, _batchCount);
                Array.Copy(arrays.Placements, placements, _batchCount);

                Transform = Matrix.Identity;
                Canvas.DrawAtlas(image, sources, placements, s_nearest, paint);

                (t_batchArrays ??= new Stack<GlyphBatchArrays>()).Push(arrays);
                _batchArrays = null;
            }

            Transform = oldTransform;
            SKPaintCache.Shared.ReturnReset(paint);
            transient?.Dispose();
            t_atlasDraws++;

            _batchCount = 0;
            _batchSingle = null;
            _batchPage = null;
        }

        private sealed class GlyphBatchArrays
        {
            public SKRect[] Sources = new SKRect[256];
            public SKRotationScaleMatrix[] Placements = new SKRotationScaleMatrix[256];
        }
    }
}
