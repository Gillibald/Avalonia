using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Contexts are made per frame, so the merged sprite arrays come from a per-thread pool,
        // as the grayscale batch's do.
        [ThreadStatic]
        private static Stack<LcdBatchArrays>? t_lcdBatchArrays;

        private LcdBatchArrays? _lcdBatch;
        private int _lcdBatchCount;
        private LcdAtlasPage? _lcdBatchPage;
        private uint _lcdBatchTint;

        /// <summary>
        /// Whether subpixel run masks on this hardware GPU context are placed in the shared
        /// <see cref="LcdRunAtlas"/>, so the runs of a frame draw with one call per page and
        /// colour. On by default; tests turn it off to draw every run from its own image and
        /// compare.
        /// </summary>
        internal bool UsesLcdRunAtlas { get; set; } = true;

        /// <summary>The atlas subpixel run masks are placed in; tests give a context a small one.</summary>
        internal LcdRunAtlas LcdAtlas { get; set; } = LcdRunAtlas.Shared;

        /// <summary>
        /// Places a subpixel run mask in the atlas when this context draws from it: a hardware
        /// GPU, where a draw that reads the destination costs far more than the pixels it
        /// shades, drawing at device scale. Returns <c>null</c> otherwise, or when the mask does
        /// not fit a page, and the caller makes the mask an image of its own.
        /// </summary>
        private LcdAtlasEntry? TryPlaceLcdMask(ReadOnlySpan<byte> rgba, int width, int height)
        {
            if (!UsesLcdRunAtlas || _glyphRasterTarget != GlyphRasterTarget.HardwareGpu || _postTransform.HasValue)
            {
                return null;
            }

            return LcdAtlas.TryAdd(rgba, width, height);
        }

        /// <summary>
        /// Draws a whole atlas entry 1:1 at <paramref name="destRect"/>, appending it to the
        /// pending subpixel batch where it can join one.
        /// </summary>
        private void DrawLcdAtlasEntry(LcdAtlasEntry entry, Rect destRect, uint tintArgb)
        {
            CheckLease();

            entry.Touch(LcdAtlas.Tick());

            var alpha = (byte)((tintArgb >> 24) * _currentOpacity);
            var tint = ((uint)alpha << 24) | (tintArgb & 0x00FFFFFF);
            var x = (int)destRect.X;
            var y = (int)destRect.Y;

            if (x != destRect.X || y != destRect.Y || !TryAppendToLcdBatch(entry, x, y, tint))
            {
                DrawLcdMask(entry, new Rect(0, 0, entry.Width, entry.Height), destRect, tintArgb,
                    new SKSamplingOptions());
            }
        }

        /// <summary>
        /// Appends an atlas entry drawn 1:1 at (<paramref name="x"/>, <paramref name="y"/>) to
        /// the pending subpixel batch, flushing first when the batch samples another page,
        /// blends another colour, holds an entry the new one would overlap, or holds as many
        /// entries as one atlas draw takes (<see cref="MaxSpritesPerAtlasDraw"/>). Returns
        /// <c>false</c> when the draw cannot be batched.
        /// </summary>
        /// <remarks>
        /// The per-channel blend reads the destination, and one draw call reads it once for all
        /// its sprites: an entry drawn over another in the same call would blend with the pixels
        /// under both instead of the first one's result. Entries of one call therefore never
        /// overlap, and each pixel of the batch is blended exactly as drawing its entry on its
        /// own would blend it.
        /// </remarks>
        private bool TryAppendToLcdBatch(LcdAtlasEntry entry, int x, int y, uint tint)
        {
            // Entries are placed in device pixels; a transform or a hidden DPI scale would move
            // them differently from the run's own placement.
            if (!BatchesGlyphAtlasDraws || _grContext is null || _postTransform.HasValue || !Transform.IsIdentity)
            {
                return false;
            }

            FlushAtlasBatch(GlyphBatchFlushReason.OtherTextPath);

            var source = SKRect.Create(entry.X, entry.Y, entry.Width, entry.Height);
            var dest = SKRect.Create(x, y, entry.Width, entry.Height);

            CountClippedRun(dest);

            if (!TryTrimToClips(ref source, ref dest))
            {
                return true;
            }

            AdmitToShapeClips(dest);

            var bounds = SKRectI.Truncate(dest);

            if (_lcdBatchCount > 0)
            {
                if (entry.Page != _lcdBatchPage)
                {
                    FlushLcdBatch(GlyphBatchFlushReason.PageChange);
                }
                else if (tint != _lcdBatchTint)
                {
                    FlushLcdBatch(GlyphBatchFlushReason.ColorChange);
                }
                else if (_lcdBatchCount == MaxSpritesPerAtlasDraw)
                {
                    FlushLcdBatch(GlyphBatchFlushReason.SpriteCap);
                }
                else if (OverlapsLcdBatch(bounds))
                {
                    FlushLcdBatch(GlyphBatchFlushReason.Overlap);
                }
            }

            AppendToLcdBatch(entry.Page, source, bounds, tint);

            return true;
        }

        /// <summary>
        /// Appends the part of an atlas entry at <paramref name="source"/> on its page, drawn 1:1
        /// to <paramref name="bounds"/>.
        /// </summary>
        private void AppendToLcdBatch(LcdAtlasPage page, SKRect source, SKRectI bounds, uint tint)
        {
            var arrays = _lcdBatch ??= t_lcdBatchArrays is { Count: > 0 } pool ? pool.Pop() : new LcdBatchArrays();

            if (_lcdBatchCount == arrays.Sources.Length)
            {
                var capacity = arrays.Sources.Length * 2;

                Array.Resize(ref arrays.Sources, capacity);
                Array.Resize(ref arrays.Placements, capacity);
                Array.Resize(ref arrays.Bounds, capacity);
            }

            arrays.Sources[_lcdBatchCount] = source;
            arrays.Placements[_lcdBatchCount] = SKRotationScaleMatrix.CreateTranslation(bounds.Left, bounds.Top);
            arrays.Bounds[_lcdBatchCount] = bounds;

            _lcdBatchCount++;
            _lcdBatchPage = page;
            _lcdBatchTint = tint;
        }

        private bool OverlapsLcdBatch(SKRectI bounds)
        {
            var pending = _lcdBatch!.Bounds;

            for (var i = 0; i < _lcdBatchCount; i++)
            {
                if (pending[i].IntersectsWith(bounds))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Draws the pending subpixel batch with one call through the colour's blender.</summary>
        private void FlushLcdBatch(GlyphBatchFlushReason reason)
        {
            if (_lcdBatchCount == 0)
            {
                return;
            }

            CountFlush(reason);
            CountBatchDrawn(reason, _lcdBatchCount);

            var page = _lcdBatchPage!;
            var arrays = _lcdBatch!;

            // A page dropped from the atlas after its entries were appended still holds their
            // coverage; its image is made for this draw alone, since the page keeps none.
            var transient = page.IsEvicted ? CreateLcdPageImage(page) : null;
            var image = transient ?? GetLcdPageImage(page);
            var paint = SKPaintCache.Shared.Get();
            var blender = LcdTextBlender.Get(_lcdBatchTint);

            if (blender is not null)
            {
                paint.Blender = blender;
            }
            else
            {
                // Unreachable by policy, as for a run mask image: eligibility requires the
                // compiled blender, but a driver losing the effect must not erase text silently.
                paint.Color = new SKColor(_lcdBatchTint);
                paint.ColorFilter = MaskGammaFilters.Get((byte)(_lcdBatchTint >> 16), (byte)(_lcdBatchTint >> 8),
                    (byte)_lcdBatchTint);
            }

            // DrawAtlas takes the sprite count from the array lengths.
            var (sources, placements) = GetTransientSpriteArrays(_lcdBatchCount);

            Array.Copy(arrays.Sources, sources, _lcdBatchCount);
            Array.Copy(arrays.Placements, placements, _lcdBatchCount);

            var oldTransform = Transform;

            Transform = Matrix.Identity;
            Canvas.DrawAtlas(image, sources, placements, s_nearest, paint);
            Transform = oldTransform;

            SKPaintCache.Shared.ReturnReset(paint);
            transient?.Dispose();
            t_atlasDraws++;

            (t_lcdBatchArrays ??= new Stack<LcdBatchArrays>()).Push(arrays);
            _lcdBatch = null;
            _lcdBatchCount = 0;
            _lcdBatchPage = null;
        }

        /// <summary>
        /// The image of a subpixel atlas page at its current version, wrapping the page's pinned
        /// array; a GPU context uploads it once per version.
        /// </summary>
        private static SKImage GetLcdPageImage(LcdAtlasPage page)
        {
            if (page.Realized is SKImage current && page.RealizedVersion == page.Version)
            {
                return current;
            }

            if (page.Realized is not null)
            {
                t_pageImagesReplaced++;
            }

            page.Realized?.Dispose();

            var image = CreateLcdPageImage(page);

            page.Realized = image;
            page.RealizedVersion = page.Version;

            return image;
        }

        private static unsafe SKImage CreateLcdPageImage(LcdAtlasPage page)
        {
            var pixels = page.Pixels;

            // Alpha holds the channel maximum, so every channel stays within it: a valid
            // premultiplied pixel that Skia passes to the blender unchanged.
            var info = new SKImageInfo(LcdRunAtlas.PageWidth, page.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var address = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));

            using var pixmap = new SKPixmap(info, address, LcdRunAtlas.PageWidth * 4);

            t_pageImagesCreated++;
            t_pageImageBytes += info.BytesSize64;

            // Placements after this point only fill rows and columns no entry of this version
            // samples, and growth moves the page to a new array, so the wrapped pixels stay what
            // this version's entries expect for as long as Skia holds the image.
            return SKImage.FromPixels(pixmap, s_releasePage, pixels);
        }

        private sealed class LcdBatchArrays
        {
            public SKRect[] Sources = new SKRect[64];
            public SKRotationScaleMatrix[] Placements = new SKRotationScaleMatrix[64];
            public SKRectI[] Bounds = new SKRectI[64];
        }
    }
}
