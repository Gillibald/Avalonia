using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Applies a proportional multiplier to the natural single-line height.
    /// </summary>
    /// <remarks>
    /// <para>Maps to:</para>
    /// <list type="bullet">
    ///     <item>DOCX: <c>w:lineRule="auto"</c> (multiplier = <c>w:line</c> / 240)</item>
    ///     <item>RTF: <c>\slmult1</c> with <c>\sl</c> value (multiplier = <c>\sl</c> / 240)</item>
    ///     <item>TOM: <c>tomLineSpaceMultiple</c>, <c>tomLineSpaceSingle</c> (1.0),
    ///           <c>tomLineSpace1pt5</c> (1.5), <c>tomLineSpaceDouble</c> (2.0)</item>
    /// </list>
    /// </remarks>
    public sealed class MultipleLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MultipleLineHeightStrategy"/> class.
        /// </summary>
        /// <param name="multiplier">
        /// The multiplier to apply to the natural line height.
        /// 1.0 = single spacing, 1.5 = 1.5 spacing, 2.0 = double spacing.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is not a positive finite value.</exception>
        public MultipleLineHeightStrategy(double multiplier)
        {
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Multiplier must be a positive finite value.");
            }

            Multiplier = multiplier;
        }

        /// <summary>
        /// Gets the multiplier applied to the natural line height.
        /// </summary>
        public double Multiplier { get; }

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            var height = m.NaturalHeight * Multiplier;
            var extra = height - (m.Descent - m.Ascent);
            return new LineHeightResult
            {
                Height = height,
                Baseline = -m.Ascent + extra / 2
            };
        }
    }
}
