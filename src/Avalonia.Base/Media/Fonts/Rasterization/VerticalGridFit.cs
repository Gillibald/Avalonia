using System;
using System.Collections.Concurrent;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A monotone piecewise-linear vertical remap in device space (y-down, baseline at 0):
    /// glyph zone positions move onto pixel boundaries and everything between interpolates.
    /// Identity outside the outer knots (slope one), so extreme points never drift.
    /// </summary>
    internal readonly struct VerticalWarp
    {
        private readonly float[]? _from;
        private readonly float[]? _to;

        public VerticalWarp(float[] from, float[] to)
        {
            _from = from;
            _to = to;
        }

        public static VerticalWarp Identity => default;

        public bool IsIdentity => _from is null || _from.Length < 2;

        public float Apply(float y)
        {
            if (_from is null || _to is null || _from.Length < 2)
            {
                return y;
            }

            if (y <= _from[0])
            {
                return y + (_to[0] - _from[0]);
            }

            var last = _from.Length - 1;

            if (y >= _from[last])
            {
                return y + (_to[last] - _from[last]);
            }

            for (var i = 1; i <= last; i++)
            {
                if (y <= _from[i])
                {
                    var t = (y - _from[i - 1]) / (_from[i] - _from[i - 1]);

                    return _to[i - 1] + t * (_to[i] - _to[i - 1]);
                }
            }

            return y;
        }
    }

    /// <summary>
    /// Vertical-only grid fitting for the mask pipeline — the light-autohint idea: the font's
    /// horizontal zones (baseline, x-height, cap height, ascender, descender) snap onto pixel
    /// rows, so crossbars, x-height tops, and baselines render as one hard row instead of two
    /// gray ones. Horizontal geometry is untouched: stems keep subpixel positioning and the
    /// LCD stripes their one-third-pixel resolution, exactly the DirectWrite-natural split.
    /// </summary>
    /// <remarks>
    /// Zones are measured once per typeface from the ink boxes of flat reference glyphs (no
    /// geometry is built), with round overshoot sized from 'o'; fonts without the reference
    /// glyphs keep whatever zones remain and degrade toward identity. Zones come from the
    /// default instance — variation instances reuse them, an accepted v1 approximation.
    /// Warps cache per scale bucket, so a mask build costs one dictionary hit.
    /// </remarks>
    internal sealed class VerticalGridFit
    {
        /// <summary>Overshoot bands flatten only while they stay visually sub-pixel.</summary>
        private const float OvershootFlattenLimit = 0.75f;

        /// <summary>
        /// Zones grow away from the baseline once their fraction reaches this, measured
        /// against DirectWrite-hinted output (Segoe UI: cap 8.40px renders 9 rows, x-height
        /// 6.50px renders 7, descender 2.76px renders 3). Plain nearest rounding — and
        /// especially banker's rounding, which sends 6.5 to 6 — reads visibly smaller.
        /// </summary>
        private const float ZoneGrowThreshold = 0.4f;

        private readonly float _designEmHeight;
        private readonly float _xHeight;
        private readonly float _capHeight;
        private readonly float _ascender;
        private readonly float _descender;
        private readonly float _roundOvershoot;
        private readonly ConcurrentDictionary<ushort, VerticalWarp> _warps = new();

        private VerticalGridFit(float designEmHeight, float xHeight, float capHeight,
            float ascender, float descender, float roundOvershoot)
        {
            _designEmHeight = designEmHeight;
            _xHeight = xHeight;
            _capHeight = capHeight;
            _ascender = ascender;
            _descender = descender;
            _roundOvershoot = roundOvershoot;
        }

        public static VerticalGridFit Create(GlyphTypeface typeface)
        {
            var xHeight = MeasureTop(typeface, 'x');
            var capHeight = MeasureTop(typeface, 'H');
            var ascender = MeasureTop(typeface, 'l');

            if (ascender <= 0)
            {
                ascender = MeasureTop(typeface, 'b');
            }

            var descender = MeasureBottom(typeface, 'p');

            if (descender >= 0)
            {
                descender = MeasureBottom(typeface, 'g');
            }

            var overshoot = 0f;
            var oTop = MeasureTop(typeface, 'o');
            var oBottom = MeasureBottom(typeface, 'o');

            if (xHeight > 0 && oTop > xHeight)
            {
                overshoot = oTop - xHeight;
            }

            if (oBottom < 0)
            {
                overshoot = Math.Max(overshoot, -oBottom);
            }

            return new VerticalGridFit(typeface.Metrics.DesignEmHeight,
                xHeight, capHeight, ascender, descender, overshoot);
        }

        /// <summary>The warp for a quantized mask scale; identity when no zones measured.</summary>
        public VerticalWarp GetWarp(ushort scaleQ)
            => _warps.GetOrAdd(scaleQ, static (key, self) => self.BuildWarp(key), this);

        private VerticalWarp BuildWarp(ushort scaleQ)
        {
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * _designEmHeight);

            // Device space is y-down with the baseline at 0 (the pen row is integer-snapped by
            // the renderer): zones above the baseline are negative.
            Span<float> zones = stackalloc float[5];
            var count = 0;

            if (_ascender > 0)
            {
                zones[count++] = -_ascender * scale;
            }

            if (_capHeight > 0)
            {
                zones[count++] = -_capHeight * scale;
            }

            if (_xHeight > 0)
            {
                zones[count++] = -_xHeight * scale;
            }

            zones[count++] = 0f;

            if (_descender < 0)
            {
                zones[count++] = -_descender * scale;
            }

            var overshoot = _roundOvershoot * scale;
            var flatten = overshoot > 0 && overshoot <= OvershootFlattenLimit;

            // Up to two knots per zone (the overshoot band flattens onto the zone) — assembled
            // in ascending device order; zones landed on the same row collapse to one knot.
            var from = new float[count * 2];
            var to = new float[count * 2];
            var knots = 0;

            for (var i = 0; i < count; i++)
            {
                var src = zones[i];
                var dst = SnapZone(src);

                if (knots > 0)
                {
                    if (src <= from[knots - 1] + 0.01f)
                    {
                        continue;   // the same position — nothing to add
                    }

                    if (src <= from[knots - 1] + 0.5f)
                    {
                        // Zones closer than a pixel (Segoe UI's ascender sits a hair above
                        // its cap at body sizes) flatten onto one row so both edges render
                        // hard, the way hinted output shares ascender and cap rows at small
                        // sizes. The LATER zone wins the row — cap height carries far more
                        // text than ascender tops — and the earlier knot is pulled along.
                        var floor = knots >= 2 ? to[knots - 2] : float.MinValue;

                        to[knots - 1] = Math.Max(dst, floor);
                        dst = to[knots - 1];
                    }
                    else if (dst < to[knots - 1])
                    {
                        dst = to[knots - 1];
                    }
                }

                // Round overshoot reaches outward from the zone: above the x-height/cap tops
                // (more negative) and below the baseline (more positive). Flattening adds the
                // band edge as a second knot mapping onto the same row — skipped when the band
                // would collide with the previous knot at very small sizes.
                var overshootsUp = src < -0.5f;
                var bandFits = knots == 0 || src - overshoot > from[knots - 1] + 0.01f;

                if (flatten && overshootsUp && bandFits)
                {
                    from[knots] = src - overshoot;
                    to[knots] = dst;
                    knots++;
                    from[knots] = src;
                    to[knots] = dst;
                    knots++;
                }
                else if (flatten && src == 0f)
                {
                    from[knots] = src;
                    to[knots] = dst;
                    knots++;
                    from[knots] = src + overshoot;
                    to[knots] = dst;
                    knots++;
                }
                else
                {
                    from[knots] = src;
                    to[knots] = dst;
                    knots++;
                }
            }

            if (knots < 2)
            {
                return VerticalWarp.Identity;
            }

            Array.Resize(ref from, knots);
            Array.Resize(ref to, knots);

            return new VerticalWarp(from, to);
        }

        /// <summary>
        /// Snaps a zone to a pixel row. Above the baseline the zone grows once its fraction
        /// reaches <see cref="ZoneGrowThreshold"/> — the measured DirectWrite behavior (cap
        /// 8.40px renders 9 rows) — while descenders round plain nearest (3.45px stays 3).
        /// The baseline itself (zero) stays fixed.
        /// </summary>
        private static float SnapZone(float src)
        {
            var magnitude = MathF.Abs(src);
            var floor = MathF.Floor(magnitude);
            var threshold = src < 0 ? ZoneGrowThreshold : 0.5f;
            var grown = magnitude - floor >= threshold ? floor + 1 : floor;

            return src < 0 ? -grown : grown;
        }

        private static float MeasureTop(GlyphTypeface typeface, char reference)
        {
            if (typeface.CharacterToGlyphMap.ContainsGlyph(reference) &&
                typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap[reference], out var box) &&
                box.YMax > box.YMin)
            {
                return box.YMax;
            }

            return 0;
        }

        private static float MeasureBottom(GlyphTypeface typeface, char reference)
        {
            if (typeface.CharacterToGlyphMap.ContainsGlyph(reference) &&
                typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap[reference], out var box) &&
                box.YMax > box.YMin)
            {
                return box.YMin;
            }

            return 0;
        }
    }
}
