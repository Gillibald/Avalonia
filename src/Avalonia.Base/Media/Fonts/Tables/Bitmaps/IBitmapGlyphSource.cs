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
    /// implementation while the glyph cache budget keeps the image, so placement lookups after
    /// the first are cheap.
    /// </summary>
    internal interface IBitmapGlyphSource
    {
        BitmapStrike SelectStrike(float pixelsPerEm);

        /// <summary>The number of colour strikes; indices run from 0 to one less.</summary>
        int StrikeCount { get; }

        /// <summary>The strike at <paramref name="index"/>.</summary>
        BitmapStrike GetStrike(int index);

        bool HasGlyphImage(ushort glyphIndex);

        /// <summary>
        /// Whether <paramref name="strike"/> holds an image for <paramref name="glyphIndex"/>. A
        /// font may leave a glyph out of some strikes (Apple Color Emoji does), so a strike that
        /// holds images for other glyphs can still lack this one.
        /// </summary>
        bool HasGlyphImage(in BitmapStrike strike, ushort glyphIndex);

        /// <summary>
        /// The pen-relative, y-down rectangle in strike pixels that the glyph's image covers in
        /// <paramref name="strike"/>, read from the strike's metrics or the image header without
        /// decoding; <c>false</c> when the strike has no image for the glyph or its size is only
        /// known after decoding.
        /// </summary>
        bool TryGetImageBounds(in BitmapStrike strike, ushort glyphIndex, out PixelRect bounds);

        bool TryGetPlacement(in BitmapStrike strike, ushort glyphIndex, IBitmapGlyphDecoder decoder,
            out BitmapGlyphPlacement placement);
    }
}
