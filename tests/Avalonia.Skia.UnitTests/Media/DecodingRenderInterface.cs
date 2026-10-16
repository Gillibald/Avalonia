using System;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The Skia render interface with the bitmap glyph decoding of <see cref="Decoder"/>, for tests
    /// that watch or replace how strike images decode.
    /// </summary>
    internal sealed class DecodingRenderInterface : PlatformRenderInterface, IBitmapGlyphDecoder
    {
        public DecodingRenderInterface(IBitmapGlyphDecoder decoder) => Decoder = decoder;

        public IBitmapGlyphDecoder Decoder { get; }

        bool IBitmapGlyphDecoder.TryDecode(ReadOnlyMemory<byte> encoded, out DecodedGlyphBitmap bitmap)
            => Decoder.TryDecode(encoded, out bitmap);
    }
}
