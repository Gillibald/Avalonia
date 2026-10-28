namespace Avalonia.Media.Fonts.Tables.Bitmaps
{
    internal static class BitmapGlyphSourceExtensions
    {
        /// <summary>
        /// Picks the strike to draw <paramref name="glyphIndex"/> from at a target device ppem: the
        /// strike <see cref="IBitmapGlyphSource.SelectStrike(float)"/> picks when it holds an image
        /// for the glyph, otherwise, by the same rule, the smallest strike at or above the request
        /// among those that hold one, else the largest of them. A font may leave a glyph out of
        /// some strikes (Apple Color Emoji does), and the size-selected strike alone would then
        /// draw nothing. Without any strike holding the glyph the size-selected strike is returned.
        /// </summary>
        public static BitmapStrike SelectStrike(this IBitmapGlyphSource source, float pixelsPerEm, ushort glyphIndex)
        {
            var selected = source.SelectStrike(pixelsPerEm);

            if (source.HasGlyphImage(selected, glyphIndex))
            {
                return selected;
            }

            BitmapStrike? best = null;
            BitmapStrike? bestAtOrAbove = null;

            for (var i = 0; i < source.StrikeCount; i++)
            {
                var strike = source.GetStrike(i);

                if (!source.HasGlyphImage(strike, glyphIndex))
                {
                    continue;
                }

                if (strike.PpemY >= pixelsPerEm && (bestAtOrAbove is not { } above || strike.PpemY < above.PpemY))
                {
                    bestAtOrAbove = strike;
                }

                if (best is not { } largest || strike.PpemY > largest.PpemY)
                {
                    best = strike;
                }
            }

            return bestAtOrAbove ?? best ?? selected;
        }
    }
}
