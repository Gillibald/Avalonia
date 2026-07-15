using System;
using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Font-wide standard stroke widths — the auto-hinter's stand-in for the CVT machinery of
    /// instructed fonts. Vertical stem widths and horizontal stroke thicknesses are measured
    /// once per typeface from reference glyphs and clustered; at snap time a detected pair
    /// whose width is within the cut-in of a standard renders at the standard's pixel width,
    /// so same-class stems share one whole width across every glyph at a given size instead
    /// of rounding apart on sub-pixel design differences (optical corrections, design
    /// scatter). Past the cut-in the natural width wins, so genuine differences survive.
    /// </summary>
    internal sealed class StemWidthTable
    {
        /// <summary>Measurement happens at this size: large enough for accurate edges, cheap.</summary>
        private const float ReferencePixels = 64f;

        /// <summary>Pair-width bounds at the reference size; the floor rejects aperture
        /// terminals and hairline noise (Inter's 'e' terminal measures 1.55 px here).</summary>
        private const float MinPairPixels = 2.5f;
        private const float MaxPairPixels = 10f;

        /// <summary>Widths within this ratio cluster onto one standard — lowercase and
        /// capital stems typically sit 3-5% apart and must share a width at text sizes.</summary>
        private const float MergeRatio = 0.08f;

        private static readonly float[] s_empty = Array.Empty<float>();

        private readonly float[] _verticalStems;
        private readonly float[] _horizontalStrokes;

        private StemWidthTable(float[] verticalStems, float[] horizontalStrokes)
        {
            _verticalStems = verticalStems;
            _horizontalStrokes = horizontalStrokes;
        }

        /// <summary>Standard vertical stem widths in design units, ascending; empty when the
        /// reference glyphs are missing or yield nothing usable.</summary>
        public ReadOnlySpan<float> VerticalStemWidths => _verticalStems;

        /// <summary>Standard horizontal stroke thicknesses in design units, ascending.</summary>
        public ReadOnlySpan<float> HorizontalStrokeWidths => _horizontalStrokes;

        public static StemWidthTable Create(GlyphTypeface typeface)
        {
            var upem = typeface.Metrics.DesignEmHeight;

            if (upem <= 0)
            {
                return new StemWidthTable(s_empty, s_empty);
            }

            var scale = ReferencePixels / upem;
            var builder = new GlyphPathBuilder();

            return new StemWidthTable(
                Measure(typeface, builder, "nmluhiHI", vertical: false, scale, upem),
                Measure(typeface, builder, "ezHAEt", vertical: true, scale, upem));
        }

        private static float[] Measure(GlyphTypeface typeface, GlyphPathBuilder builder,
            string references, bool vertical, float scale, float designEmHeight)
        {
            var samples = new List<float>();
            Span<float> widths = stackalloc float[8];

            foreach (var reference in references)
            {
                if (!typeface.CharacterToGlyphMap.ContainsGlyph(reference))
                {
                    continue;
                }

                builder.Reset();

                if (!typeface.TryBuildGlyphContours(typeface.CharacterToGlyphMap[reference],
                        new Matrix(scale, 0, 0, -scale, 0, 0), builder))
                {
                    continue;
                }

                var count = StemFit.MeasurePairWidths(builder, vertical, MinPairPixels, MaxPairPixels, widths);

                for (var i = 0; i < count; i++)
                {
                    samples.Add(widths[i]);
                }
            }

            if (samples.Count < 2)
            {
                return s_empty;
            }

            samples.Sort();

            // Single-link clustering with a relative gap; singleton clusters are noise.
            var standards = new List<float>(3);
            var clusterStart = 0;

            for (var i = 1; i <= samples.Count; i++)
            {
                if (i < samples.Count && samples[i] - samples[i - 1] <= samples[i - 1] * MergeRatio + 0.15f)
                {
                    continue;
                }

                var members = i - clusterStart;

                if (members >= 2 && standards.Count < 3)
                {
                    var sum = 0f;

                    for (var j = clusterStart; j < i; j++)
                    {
                        sum += samples[j];
                    }

                    standards.Add(sum / members * designEmHeight / ReferencePixels);
                }

                clusterStart = i;
            }

            return standards.Count == 0 ? s_empty : standards.ToArray();
        }
    }
}
