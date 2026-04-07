using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Uses natural metrics but enforces a minimum line height.
    /// The line height is never smaller than the specified minimum.
    /// </summary>
    /// <remarks>
    /// <para>Maps to:</para>
    /// <list type="bullet">
    ///     <item>DOCX: <c>w:lineRule="atLeast"</c></item>
    ///     <item>RTF: <c>\sl</c> (positive value)</item>
    ///     <item>TOM: <c>tomLineSpaceAtLeast</c></item>
    /// </list>
    /// </remarks>
    public sealed class AtLeastLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AtLeastLineHeightStrategy"/> class.
        /// </summary>
        /// <param name="minimumHeight">The minimum line height in device-independent pixels.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumHeight"/> is not a positive finite value.</exception>
        public AtLeastLineHeightStrategy(double minimumHeight)
        {
            if (double.IsNaN(minimumHeight) || double.IsInfinity(minimumHeight) || minimumHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumHeight), minimumHeight, "MinimumHeight must be a positive finite value.");
            }

            MinimumHeight = minimumHeight;
        }

        /// <summary>
        /// Gets the minimum line height in device-independent pixels.
        /// </summary>
        public double MinimumHeight { get; }

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            if (m.NaturalHeight >= MinimumHeight)
            {
                return new LineHeightResult
                {
                    Height = m.NaturalHeight,
                    Baseline = m.NaturalBaseline
                };
            }

            var extra = MinimumHeight - (m.Descent - m.Ascent);
            return new LineHeightResult
            {
                Height = MinimumHeight,
                Baseline = -m.Ascent + extra / 2
            };
        }
    }
}
