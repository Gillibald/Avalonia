using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
            bool gamma, bool bilinear)
        {
            CheckLease();

            var backend = (SkiaGlyphAtlasBatch)batch.Backend;
            var image = backend.Image ?? GetPageImage(batch.Page!);
            var paint = SKPaintCache.Shared.Get();
            var r = (byte)(tintArgb >> 16);
            var g = (byte)(tintArgb >> 8);
            var b = (byte)tintArgb;

            paint.Color = new SKColor(r, g, b, (byte)((tintArgb >> 24) * _currentOpacity));

            // Coverage lands in alpha (the A8 page modulated by the paint colour), so the
            // coverage correction rides a colour filter on alpha, as for single alpha masks.
            if (gamma)
            {
                paint.ColorFilter = MaskGammaFilters.Get(r, g, b);
            }

            var oldTransform = Transform;

            Transform = transform;
            Canvas.DrawAtlas(image, backend.Sources, backend.Placements, bilinear ? s_bilinear : s_nearest, paint);
            Transform = oldTransform;

            paint.ColorFilter = null;
            SKPaintCache.Shared.ReturnReset(paint);
        }

        /// <summary>
        /// The image of an atlas page at its current version. The image wraps the page's
        /// pinned array without copying; a GPU context uploads it once per version and draws
        /// every batch on the page from that texture.
        /// </summary>
        private static unsafe SKImage GetPageImage(GlyphAtlasPage page)
        {
            if (page.Realized is SKImage current && page.RealizedVersion == page.Version)
            {
                return current;
            }

            page.Realized?.Dispose();

            var pixels = page.Pixels;
            var info = new SKImageInfo(GlyphMaskAtlas.PageWidth, page.Height, SKColorType.Alpha8, SKAlphaType.Premul);
            var address = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));

            using var pixmap = new SKPixmap(info, address, GlyphMaskAtlas.PageWidth);

            // Writes after this point only fill rows and columns no sprite of this version
            // samples, and growth moves the page to a new array, so the wrapped pixels stay
            // what this version's sprites expect for as long as Skia holds the image.
            var image = SKImage.FromPixels(pixmap, s_releasePage, pixels);

            page.Realized = image;
            page.RealizedVersion = page.Version;

            return image;
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
