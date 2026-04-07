using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// All lines share the same height based on the paragraph's default font metrics only.
    /// Fallback fonts do not affect line height. An optional multiplier may be applied.
    /// </summary>
    /// <remarks>
    /// This matches browser CSS <c>line-height: normal</c> behavior where the line box height
    /// is determined by the element's font, not by fallback fonts used for individual characters.
    /// Best for UI text, labels, and paragraphs that need uniform spacing.
    /// </remarks>
    public sealed class UniformLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UniformLineHeightStrategy"/> class.
        /// </summary>
        /// <param name="multiplier">
        /// The multiplier to apply to the default font's natural height. Defaults to 1.0.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is not a positive finite value.</exception>
        public UniformLineHeightStrategy(double multiplier = 1.0)
        {
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Multiplier must be a positive finite value.");
            }

            Multiplier = multiplier;
        }

        /// <summary>
        /// Gets the multiplier applied to the default font's natural height.
        /// </summary>
        public double Multiplier { get; }

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            var defaultNaturalHeight = m.DefaultFontDescent - m.DefaultFontAscent + m.DefaultFontLineGap;
            var uniformHeight = defaultNaturalHeight * Multiplier;

            // Never clip actual ink content
            var height = Math.Max(uniformHeight, m.InkExtent);

            var defaultBaseline = -m.DefaultFontAscent + m.DefaultFontLineGap * 0.5;
            var baseline = defaultBaseline * Multiplier;

            return new LineHeightResult
            {
                Height = height,
                Baseline = baseline
            };
        }
    }
}
