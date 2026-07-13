using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Media.Fonts.Tables.Bitmaps
{
    /// <summary>
    /// A decoded strike glyph with its placement: pen-relative, y-down, in strike pixels.
    /// CBDT bearings and sbix origin offsets both normalize to this shape.
    /// </summary>
    internal readonly struct BitmapGlyphPlacement
    {
        public BitmapGlyphPlacement(DecodedGlyphBitmap bitmap, int left, int top)
        {
            Bitmap = bitmap;
            Left = left;
            Top = top;
        }

        public DecodedGlyphBitmap Bitmap { get; }
        public int Left { get; }
        public int Top { get; }
    }

    /// <summary>
    /// The format-independent surface the managed pipeline consumes for bitmap strikes,
    /// implemented by CBDT/CBLC and sbix. Decoding memoises per (glyph, strike) inside the
    /// implementation, so placement lookups after the first are cheap.
    /// </summary>
    internal interface IBitmapGlyphSource
    {
        BitmapStrike SelectStrike(float pixelsPerEm);

        bool HasGlyphImage(ushort glyphIndex);

        bool TryGetPlacement(in BitmapStrike strike, ushort glyphIndex, IBitmapGlyphDecoder decoder,
            out BitmapGlyphPlacement placement);
    }
}
