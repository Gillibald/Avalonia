using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Uses natural metrics but clamps the line height to a maximum value.
    /// Never clamps below the ink bounds extent to avoid clipping visible content.
    /// </summary>
    /// <remarks>
    /// Useful for modern UI that mixes scripts and emoji where fallback fonts can produce
    /// unexpectedly large line heights. The clamp prevents layout blowup while still
    /// ensuring all drawn content remains visible.
    /// </remarks>
    public sealed class ClampedLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClampedLineHeightStrategy"/> class.
        /// </summary>
        /// <param name="maximumHeight">The maximum line height in device-independent pixels.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumHeight"/> is not a positive finite value.</exception>
        public ClampedLineHeightStrategy(double maximumHeight)
        {
            if (double.IsNaN(maximumHeight) || double.IsInfinity(maximumHeight) || maximumHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumHeight), maximumHeight, "MaximumHeight must be a positive finite value.");
            }

            MaximumHeight = maximumHeight;
        }

        /// <summary>
        /// Gets the maximum line height in device-independent pixels.
        /// </summary>
        public double MaximumHeight { get; }

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            var height = Math.Min(m.NaturalHeight, MaximumHeight);

            // Never clip actual ink content
            height = Math.Max(height, m.InkExtent);

            var extra = height - (m.Descent - m.Ascent);
            return new LineHeightResult
            {
                Height = height,
                Baseline = -m.Ascent + Math.Max(extra / 2, 0)
            };
        }
    }
}
