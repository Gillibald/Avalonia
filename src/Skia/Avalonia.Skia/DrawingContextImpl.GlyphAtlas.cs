using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        private static readonly SKSamplingOptions s_nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
        private static readonly SKSamplingOptions s_bilinear = new(SKFilterMode.Linear, SKMipmapMode.None);

        // The release callback only exists to keep the page array reachable until Skia drops
        // its last reference to an image wrapping it; the array is its context object.
        private static readonly SKImageRasterReleaseDelegate s_releasePage = static (_, _) => { };

        [ThreadStatic]
        private static SKPixmap? t_blitPixmap;

        /// <summary>
        /// Whether text may be blended straight into this context's raster surface. On by
        /// default; tests turn it off to compare with drawing through the canvas.
        /// </summary>
        internal bool AllowsDirectSurfaceWrites { get; set; } = true;

        bool ITransformedGlyphContext.TryGetBlitTarget(out GlyphBlitTarget target)
        {
            target = default;

            if (!AllowsDirectSurfaceWrites)
            {
                return false;
            }

            // Writing the surface directly is only equivalent to drawing through the canvas
            // when the canvas would write the same pixels 1:1: no layer or opacity between the
            // draw and the surface, source-over blending, device coordinates equal to canvas
            // coordinates, and a clip that is exactly its bounds.
            if (_grContext is not null || Surface is null || _saveLayerDepth != 0 || _currentOpacity != 1 ||
                _postTransform.HasValue || !Canvas.IsClipRect ||
                RenderOptions.BitmapBlendingMode is not (BitmapBlendingMode.Unspecified or BitmapBlendingMode.SourceOver))
            {
                return false;
            }

            var pixmap = t_blitPixmap ??= new SKPixmap();

            if (!Surface.PeekPixels(pixmap) ||
                pixmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888) ||
                pixmap.AlphaType is not (SKAlphaType.Premul or SKAlphaType.Opaque))
            {
                return false;
            }

            var clip = Canvas.DeviceClipBounds;

            // Skia's sprite blitter takes a BGRA bitmap drawn 1:1 when the surface holds the
            // platform's native BGRA order; onto any other surface its raster pipeline converts.
            target = new GlyphBlitTarget(pixmap.GetPixels(), pixmap.RowBytes, pixmap.Width, pixmap.Height,
                new PixelRect(clip.Left, clip.Top, Math.Max(0, clip.Width), Math.Max(0, clip.Height)),
                pixmap.ColorType == SKColorType.Rgba8888,
                pixmap.ColorType == SKColorType.Bgra8888 && SKImageInfo.PlatformColorType == SKColorType.Bgra8888);

            return true;
        }

        IDisposable ITransformedGlyphContext.CreateAtlasBatch(ReadOnlySpan<GlyphAtlasSprite> sprites,
            GlyphMask? standalone)
        {
            var sources = new SKRect[sprites.Length];
            var placements = new SKRotationScaleMatrix[sprites.Length];

            for (var i = 0; i < sprites.Length; i++)
            {
                var sprite = sprites[i];

                sources[i] = SKRect.Create(sprite.SourceX, sprite.SourceY, sprite.Width, sprite.Height);
                placements[i] = SKRotationScaleMatrix.CreateTranslation(sprite.X, sprite.Y);
            }

            SKImage? image = null;

            if (standalone is not null)
            {
                var info = new SKImageInfo(standalone.Width, standalone.Height, SKColorType.Alpha8, SKAlphaType.Premul);

                image = SKImage.FromPixelCopy(info, standalone.Alpha, standalone.Width);
            }

            return new SkiaGlyphAtlasBatch(sources, placements, image);
        }

        void ITransformedGlyphContext.DrawAtlasBatch(GlyphAtlasBatch batch, in Matrix transform, uint tintArgb,
            bool bilinear)
        {
            CheckLease();

            var backend = (SkiaGlyphAtlasBatch)batch.Backend;

            // The A8 page holds coverage already corrected for this colour's luminance, and
            // drawing modulates it by the paint colour.
            var color = new SKColor((byte)(tintArgb >> 16), (byte)(tintArgb >> 8), (byte)tintArgb,
                (byte)((tintArgb >> 24) * _currentOpacity));

            if (!bilinear && backend.Image is null && batch.Page is { } page &&
                TryAppendToGlyphBatch(page, backend, transform, color))
            {
                return;
            }

            FlushGlyphBatch();

            var image = backend.Image ?? GetPageImage(batch.Page!);
            var paint = SKPaintCache.Shared.Get();

            paint.Color = color;

            var oldTransform = Transform;

            Transform = transform;
            Canvas.DrawAtlas(image, backend.Sources, backend.Placements, bilinear ? s_bilinear : s_nearest, paint);
            t_atlasDraws++;
            t_atlasGeometry += backend.Sources.Length;
            Transform = oldTransform;

            SKPaintCache.Shared.ReturnReset(paint);
        }

        IDisposable ITransformedGlyphContext.CreateTransientImage(ReadOnlySpan<byte> coverage, int width, int height,
            int rowBytes)
        {
            // A copy: Skia uploads a raster image when the GPU work is flushed, after the
            // caller has reused its buffer for the next run.
            var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

            return SKImage.FromPixelCopy(info, coverage, rowBytes) ??
                   throw new InvalidOperationException("Could not create a transient glyph image.");
        }

        void ITransformedGlyphContext.DrawTransientSprites(IDisposable image, ReadOnlySpan<GlyphAtlasSprite> sprites,
            in Matrix transform, uint tintArgb)
        {
            PrepareCanvas();

            // DrawAtlas takes the sprite count from the array lengths, so the arrays are exact
            // fits, kept per length: a steady animation draws the same sprite counts frame
            // after frame and allocates none. Skia copies the geometry during the call.
            var (sources, placements) = GetTransientSpriteArrays(sprites.Length);

            for (var i = 0; i < sprites.Length; i++)
            {
                var sprite = sprites[i];

                sources[i] = SKRect.Create(sprite.SourceX, sprite.SourceY, sprite.Width, sprite.Height);
                placements[i] = SKRotationScaleMatrix.CreateTranslation(sprite.X, sprite.Y);
            }

            var paint = SKPaintCache.Shared.Get();

            paint.Color = new SKColor((byte)(tintArgb >> 16), (byte)(tintArgb >> 8), (byte)tintArgb,
                (byte)((tintArgb >> 24) * _currentOpacity));

            var oldTransform = Transform;

            Transform = transform;
            Canvas.DrawAtlas((SKImage)image, sources, placements, s_nearest, paint);
            t_atlasGeometry += sprites.Length;
            Transform = oldTransform;

            SKPaintCache.Shared.ReturnReset(paint);
        }

        private const int MaxTransientSpriteArrayLengths = 64;

        [ThreadStatic]
        private static Dictionary<int, (SKRect[] Sources, SKRotationScaleMatrix[] Placements)>? t_transientSprites;

        private static (SKRect[] Sources, SKRotationScaleMatrix[] Placements) GetTransientSpriteArrays(int length)
        {
            var arrays = t_transientSprites ??= new Dictionary<int, (SKRect[], SKRotationScaleMatrix[])>();

            if (arrays.TryGetValue(length, out var pair))
            {
                return pair;
            }

            // Lengths that only appeared once, during a relayout say, do not pile up.
            if (arrays.Count >= MaxTransientSpriteArrayLengths)
            {
                arrays.Clear();
            }

            pair = (new SKRect[length], new SKRotationScaleMatrix[length]);
            arrays.Add(length, pair);

            return pair;
        }

        /// <summary>
        /// The image of an atlas page at its current version. The image wraps the page's
        /// pinned array without copying; a GPU context uploads it once per version and draws
        /// every batch on the page from that texture.
        /// </summary>
        private static SKImage GetPageImage(GlyphAtlasPage page)
        {
            if (page.Realized is SKImage current && page.RealizedVersion == page.Version)
            {
                return current;
            }

            page.Realized?.Dispose();

            var image = CreatePageImage(page);

            page.Realized = image;
            page.RealizedVersion = page.Version;

            return image;
        }

        [ThreadStatic]
        private static int t_pageImagesCreated;

        [ThreadStatic]
        private static int t_atlasDraws;

        /// <summary>
        /// The number of atlas page images made on this thread, each a texture upload on a GPU
        /// context; for tests.
        /// </summary>
        internal static int PageImagesCreatedOnThread => t_pageImagesCreated;

        /// <summary>The number of atlas draw calls issued on this thread; for tests.</summary>
        internal static int AtlasDrawsOnThread => t_atlasDraws;

        [ThreadStatic]
        private static int t_atlasGeometry;

        /// <summary>
        /// The number of glyph atlas sprites whose geometry was handed to Skia on this thread;
        /// sprites drawn from geometry kept from an earlier frame do not count. For tests.
        /// </summary>
        internal static int AtlasGeometrySubmittedOnThread => t_atlasGeometry;

        private static unsafe SKImage CreatePageImage(GlyphAtlasPage page)
        {
            var pixels = page.Pixels;
            var info = new SKImageInfo(GlyphMaskAtlas.PageWidth, page.Height, SKColorType.Alpha8, SKAlphaType.Premul);
            var address = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));

            using var pixmap = new SKPixmap(info, address, GlyphMaskAtlas.PageWidth);

            t_pageImagesCreated++;

            // Writes after this point only fill rows and columns no sprite of this version
            // samples, and growth moves the page to a new array, so the wrapped pixels stay
            // what this version's sprites expect for as long as Skia holds the image.
            return SKImage.FromPixels(pixmap, s_releasePage, pixels);
        }

        /// <summary>The exact-length sprite arrays of one batch, plus its own image for a standalone glyph.</summary>
        internal sealed class SkiaGlyphAtlasBatch : IDisposable
        {
            public SkiaGlyphAtlasBatch(SKRect[] sources, SKRotationScaleMatrix[] placements, SKImage? image)
            {
                Sources = sources;
                Placements = placements;
                Image = image;
            }

            public SKRect[] Sources { get; }

            public SKRotationScaleMatrix[] Placements { get; }

            public SKImage? Image { get; }

            public void Dispose() => Image?.Dispose();
        }
    }
}
