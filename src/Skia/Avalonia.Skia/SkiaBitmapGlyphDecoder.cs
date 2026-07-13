using System;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Decodes embedded glyph images (CBDT/sbix PNG payloads) through Skia's codecs into the
    /// premultiplied BGRA pixels the run-mask composer consumes. Registered by
    /// <see cref="SkiaPlatform"/>; a managed codec can take over the binding to make bitmap
    /// glyphs backend-free.
    /// </summary>
    internal sealed class SkiaBitmapGlyphDecoder : IBitmapGlyphDecoder
    {
        public bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
        {
            bitmap = default;

            try
            {
                using var data = SKData.CreateCopy(encoded.Span);
                using var decoded = SKBitmap.Decode(data);

                if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                {
                    return false;
                }

                var info = new SKImageInfo(decoded.Width, decoded.Height,
                    SKColorType.Bgra8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                using var pixmap = decoded.PeekPixels();

                if (pixmap is null)
                {
                    return false;
                }

                unsafe
                {
                    fixed (byte* target = pixels)
                    {
                        if (!pixmap.ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0))
                        {
                            return false;
                        }
                    }
                }

                bitmap = new DecodedGlyphBitmap(pixels, decoded.Width, decoded.Height);
                return true;
            }
            catch (Exception)
            {
                // Hostile or unsupported image payloads degrade to "no image" — the outline
                // fallback keeps rendering, matching the table hardening rules.
                return false;
            }
        }
    }
}
