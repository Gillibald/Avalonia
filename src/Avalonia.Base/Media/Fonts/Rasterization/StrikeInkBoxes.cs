using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Bitmaps;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The box each glyph's bitmap strike images cover, per glyph of one typeface's strike source,
    /// in ems from the pen with y down: the union of its image rectangles in every strike, each
    /// scaled by its strike's size, grown by half a texel of the smallest strike, where resampling
    /// fades the edges. Read from the strike metrics or image headers once per glyph and kept by
    /// the typeface (<see cref="GlyphTypeface.StrikeInkBoxes"/>).
    /// </summary>
    internal sealed class StrikeInkBoxes
    {
        private readonly IBitmapGlyphSource _bitmaps;
        private readonly Dictionary<ushort, Rect?> _boxes = new();
        private readonly double _fade;

        public StrikeInkBoxes(IBitmapGlyphSource bitmaps)
        {
            _bitmaps = bitmaps;

            var smallest = int.MaxValue;

            for (var s = 0; s < bitmaps.StrikeCount; s++)
            {
                smallest = Math.Min(smallest, bitmaps.GetStrike(s).PpemY);
            }

            _fade = smallest is > 0 and < int.MaxValue ? 0.5 / smallest : 0;
        }

        /// <summary>
        /// The em box of <paramref name="glyph"/>'s images; <c>false</c> when a strike holds an
        /// image whose size is only known from decoding it. A glyph without images has an empty box.
        /// </summary>
        public bool TryGet(ushort glyph, out Rect box)
        {
            Rect? known;

            lock (_boxes)
            {
                if (!_boxes.TryGetValue(glyph, out known))
                {
                    known = Measure(glyph);
                    _boxes.Add(glyph, known);
                }
            }

            box = known ?? default;

            return known is not null;
        }

        private Rect? Measure(ushort glyph)
        {
            if (!_bitmaps.HasGlyphImage(glyph))
            {
                return default(Rect);
            }

            Rect? union = null;

            for (var s = 0; s < _bitmaps.StrikeCount; s++)
            {
                var strike = _bitmaps.GetStrike(s);

                // A strike without an image for the glyph adds nothing: a font may leave a glyph
                // out of some strikes (Apple Color Emoji does).
                if (strike.PpemY == 0 || !_bitmaps.HasGlyphImage(strike, glyph))
                {
                    continue;
                }

                if (!_bitmaps.TryGetImageBounds(strike, glyph, out var image))
                {
                    // A strike holds an image of unknown size: no box bounds it.
                    return null;
                }

                var ems = 1.0 / strike.PpemY;
                var rect = new Rect(image.X * ems, image.Y * ems, image.Width * ems, image.Height * ems)
                    .Inflate(_fade);

                union = union is { } u ? u.Union(rect) : rect;
            }

            return union ?? default(Rect);
        }
    }
}
