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
        public unsafe bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
        {
            bitmap = default;

            try
            {
                // The codec reads the payload where the font keeps it and writes the texels straight
                // into the array the composer reads: the array is the decode's only allocation.
                using var pin = encoded.Pin();
                using var data = SKData.Create((IntPtr)pin.Pointer, encoded.Length, null);
                using var codec = SKCodec.Create(data);

                if (codec is null)
                {
                    return false;
                }

                var size = codec.Info;

                if (size.Width <= 0 || size.Height <= 0)
                {
                    return false;
                }

                var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];
                SKCodecResult result;

                fixed (byte* target = pixels)
                {
                    result = codec.GetPixels(info, (IntPtr)target, info.RowBytes, default);
                }

                // An incomplete image keeps the rows the codec could decode, as a bitmap decode does.
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    return false;
                }

                bitmap = new DecodedGlyphBitmap(pixels, size.Width, size.Height);
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
