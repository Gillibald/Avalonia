using System;
using System.Collections.Concurrent;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A monotone piecewise-linear vertical remap in device space (y-down, baseline at 0):
    /// glyph zone positions move onto pixel boundaries and everything between interpolates.
    /// Identity outside the outer knots (slope one), so extreme points never drift.
    /// </summary>
    internal readonly struct AxisWarp
    {
        private readonly float[]? _from;
        private readonly float[]? _to;

        public AxisWarp(float[] from, float[] to)
        {
            _from = from;
            _to = to;
        }

        public static AxisWarp Identity => default;

        public bool IsIdentity => _from is null || _from.Length < 2;

        /// <summary>The source knots, ascending; empty when identity.</summary>
        internal ReadOnlySpan<float> From => _from;

        /// <summary>The mapped knots, non-decreasing, parallel to <see cref="From"/>.</summary>
        internal ReadOnlySpan<float> To => _to;

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
        private readonly float _ascenderOvershoot;
        private readonly ConcurrentDictionary<ushort, AxisWarp> _warps = new();

        private VerticalGridFit(float designEmHeight, float xHeight, float capHeight,
            float ascender, float descender, float roundOvershoot, float ascenderOvershoot)
        {
            _designEmHeight = designEmHeight;
            _xHeight = xHeight;
            _capHeight = capHeight;
            _ascender = ascender;
            _descender = descender;
            _roundOvershoot = roundOvershoot;
            _ascenderOvershoot = ascenderOvershoot;
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

            // The f hook typically clears the ascender by a sliver (Segoe UI 22, Inter 96
            // design units) that is a design overshoot of the ascender line, not a line of
            // its own. Anything larger (t, which owns its height) stays untouched.
            var ascenderOvershoot = 0f;
            var fTop = MeasureTop(typeface, 'f');
            var designEmHeight = typeface.Metrics.DesignEmHeight;

            if (ascender > 0 && fTop > ascender && fTop - ascender <= designEmHeight / 24f)
            {
                ascenderOvershoot = fTop - ascender;
            }

            return new VerticalGridFit(designEmHeight,
                xHeight, capHeight, ascender, descender, overshoot, ascenderOvershoot);
        }

        /// <summary>The zone warp for a quantized mask scale; identity when no zones measured.</summary>
        public AxisWarp GetWarp(ushort scaleQ)
            => _warps.GetOrAdd(scaleQ, static (key, self) => self.BuildWarp(key), this);

        /// <summary>
        /// The per-glyph warp: the cached zone knots refined with this glyph's own horizontal
        /// stroke pairs (crossbars, arms, bowl waists), which snap position to a row and
        /// thickness to whole rows. Zones anchor the extremes; stroke pairs keep interior
        /// strokes crisp and thickness-true, so interpolation only ever stretches the empty
        /// counter space between features instead of the strokes themselves.
        /// </summary>
        public AxisWarp GetGlyphWarp(GlyphPathBuilder contours, ushort scaleQ,
            ReadOnlySpan<float> strokeStandards)
        {
            var zones = GetWarp(scaleQ);

            if (zones.IsIdentity)
            {
                return zones;
            }

            var designToPixels = scaleQ / (GlyphMaskKey.ScaleQuantum * _designEmHeight);
            Span<float> strokeFrom = stackalloc float[16];
            Span<float> strokeTo = stackalloc float[16];
            var strokeKnots = StemFit.CollectStrokeKnots(contours, zones.From, 0.75f,
                strokeStandards, designToPixels, strokeFrom, strokeTo);

            if (strokeKnots == 0)
            {
                return zones;
            }

            // Merge, zone knots authoritative: a stroke pair is inserted only where it fits
            // strictly between existing knots in both source and target order; a pair that
            // does not fit whole is dropped whole (half-moved strokes would distort).
            var zoneFrom = zones.From;
            var zoneTo = zones.To;
            var mergedFrom = new float[zoneFrom.Length + strokeKnots];
            var mergedTo = new float[zoneFrom.Length + strokeKnots];

            zoneFrom.CopyTo(mergedFrom);
            zoneTo.CopyTo(mergedTo);

            var count = zoneFrom.Length;

            for (var i = 0; i + 1 < strokeKnots; i += 2)
            {
                TryInsert(mergedFrom, mergedTo, ref count,
                    strokeFrom[i], strokeTo[i], strokeFrom[i + 1], strokeTo[i + 1]);
            }

            if (count == zoneFrom.Length)
            {
                return zones;
            }

            var finalFrom = new float[count];
            var finalTo = new float[count];

            Array.Copy(mergedFrom, finalFrom, count);
            Array.Copy(mergedTo, finalTo, count);
            Array.Sort(finalFrom, finalTo);

            return new AxisWarp(finalFrom, finalTo);
        }

        private static void TryInsert(float[] from, float[] to, ref int count,
            float srcTop, float dstTop, float srcBottom, float dstBottom)
        {
            var lowerDst = float.MinValue;
            var upperDst = float.MaxValue;

            for (var i = 0; i < count; i++)
            {
                if (MathF.Abs(from[i] - srcTop) < 0.25f || MathF.Abs(from[i] - srcBottom) < 0.25f)
                {
                    return;   // touches an existing knot — the zone side owns it
                }

                if (from[i] < srcTop && to[i] > lowerDst)
                {
                    lowerDst = to[i];
                }

                if (from[i] > srcBottom && to[i] < upperDst)
                {
                    upperDst = to[i];
                }

                if (from[i] > srcTop && from[i] < srcBottom)
                {
                    return;   // a knot inside the stroke — bail rather than shear it
                }
            }

            if (dstTop < lowerDst || dstBottom > upperDst)
            {
                return;   // would break target monotonicity against the zones
            }

            from[count] = srcTop;
            to[count] = dstTop;
            count++;
            from[count] = srcBottom;
            to[count] = dstBottom;
            count++;
        }

        private AxisWarp BuildWarp(ushort scaleQ)
        {
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * _designEmHeight);

            // Device space is y-down with the baseline at 0 (the pen row is integer-snapped by
            // the renderer): zones above the baseline are negative. Each zone carries its own
            // overshoot band: the round overshoot ('o', 'O', '8') for x-height, cap and
            // baseline, plus the f hook's sliver for the ascender.
            Span<float> src = stackalloc float[5];
            Span<float> dst = stackalloc float[5];
            Span<float> band = stackalloc float[5];
            var count = 0;

            if (_ascender > 0)
            {
                src[count] = -_ascender * scale;
                band[count++] = Math.Max(_roundOvershoot, _ascenderOvershoot) * scale;
            }

            if (_capHeight > 0)
            {
                src[count] = -_capHeight * scale;
                band[count++] = _roundOvershoot * scale;
            }

            if (_xHeight > 0)
            {
                src[count] = -_xHeight * scale;
                band[count++] = _roundOvershoot * scale;
            }

            src[count] = 0f;
            band[count++] = _roundOvershoot * scale;

            if (_descender < 0)
            {
                src[count] = -_descender * scale;
                band[count++] = 0f;
            }

            for (var i = 0; i < count; i++)
            {
                dst[i] = SnapZone(src[i]);
            }

            // Resolve collisions before emitting: DISTINCT zones closer than half a pixel
            // cannot hold distinct rows, so the cluster shares one — at plain nearest of its
            // topmost member, which is how hinted DirectWrite output places Segoe UI's shared
            // ascender/cap line at small sizes (9 px: round(6.66) = 7, never the cap's 6).
            // Growing the merged line by the usual threshold would overshoot: the merged ink
            // sits between the two sources. Zones at the SAME height (Arial and Inter put
            // caps and ascender both on one line) are one line, not a collision — they keep
            // the calibrated grow policy and collapse at emission.
            for (var i = 1; i < count && src[i] < -0.5f; i++)
            {
                var gap = src[i] - src[i - 1];

                if (gap > 0.01f && gap <= 0.5f)
                {
                    var clusterStart = i - 1;

                    while (clusterStart > 0)
                    {
                        var previousGap = src[clusterStart] - src[clusterStart - 1];

                        if (previousGap <= 0.01f || previousGap > 0.5f)
                        {
                            break;
                        }

                        clusterStart--;
                    }

                    var merged = -MathF.Floor(-src[clusterStart] + 0.5f);

                    for (var j = clusterStart; j <= i; j++)
                    {
                        dst[j] = merged;
                    }
                }
            }

            // Monotonicity: a zone may never land above the one before it.
            for (var i = 1; i < count; i++)
            {
                if (dst[i] < dst[i - 1])
                {
                    dst[i] = dst[i - 1];
                }
            }

            // Emit knots in ascending device order; overshoot bands flatten onto the zone row
            // while they stay visually sub-pixel, and are skipped where they would collide
            // with the previous knot at very small sizes.
            var from = new float[count * 2];
            var to = new float[count * 2];
            var knots = 0;

            for (var i = 0; i < count; i++)
            {
                if (knots > 0 && src[i] <= from[knots - 1] + 0.01f)
                {
                    continue;   // the same position — nothing to add
                }

                var flatten = band[i] > 0 && band[i] <= OvershootFlattenLimit;
                var bandFits = knots == 0 || src[i] - band[i] > from[knots - 1] + 0.01f;

                if (flatten && src[i] < -0.5f && bandFits)
                {
                    from[knots] = src[i] - band[i];
                    to[knots] = dst[i];
                    knots++;
                    from[knots] = src[i];
                    to[knots] = dst[i];
                    knots++;
                }
                else if (flatten && src[i] == 0f)
                {
                    from[knots] = src[i];
                    to[knots] = dst[i];
                    knots++;
                    from[knots] = src[i] + band[i];
                    to[knots] = dst[i];
                    knots++;
                }
                else
                {
                    from[knots] = src[i];
                    to[knots] = dst[i];
                    knots++;
                }
            }

            if (knots < 2)
            {
                return AxisWarp.Identity;
            }

            Array.Resize(ref from, knots);
            Array.Resize(ref to, knots);

            return new AxisWarp(from, to);
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
