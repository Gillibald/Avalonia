using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>A decoded bitmap glyph strike image: premultiplied BGRA, tightly packed.</summary>
    internal readonly struct DecodedGlyphBitmap
    {
        public DecodedGlyphBitmap(byte[] bgra, int width, int height)
        {
            Bgra = bgra;
            Width = width;
            Height = height;
        }

        public byte[] Bgra { get; }
        public int Width { get; }
        public int Height { get; }
    }

    /// <summary>
    /// Decodes embedded glyph images (CBDT/sbix PNG payloads) to premultiplied BGRA pixels.
    /// Resolved through the locator: the Skia backend registers a codec-backed implementation,
    /// and a managed codec (the universal bitmap infrastructure) can replace it to make bitmap
    /// glyphs fully backend-free. Decoding is a cold-path operation (compose misses only).
    /// </summary>
    internal interface IBitmapGlyphDecoder
    {
        bool TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap);
    }
}
