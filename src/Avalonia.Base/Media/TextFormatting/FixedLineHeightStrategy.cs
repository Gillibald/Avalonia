using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Uses a fixed height for every line. Text may clip if the height is smaller than natural.
    /// </summary>
    /// <remarks>
    /// <para>Maps to:</para>
    /// <list type="bullet">
    ///     <item>DOCX: <c>w:lineRule="exact"</c></item>
    ///     <item>RTF: <c>\sl</c> (negative value, absolute)</item>
    ///     <item>TOM: <c>tomLineSpaceExactly</c></item>
    /// </list>
    /// </remarks>
    public sealed class FixedLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FixedLineHeightStrategy"/> class.
        /// </summary>
        /// <param name="height">The fixed line height in device-independent pixels.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> is not a positive finite value.</exception>
        public FixedLineHeightStrategy(double height)
        {
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be a positive finite value.");
            }

            Height = height;
        }

        /// <summary>
        /// Gets the fixed line height in device-independent pixels.
        /// </summary>
        public double Height { get; }

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            if (Height <= m.NaturalHeight)
            {
                return new LineHeightResult
                {
                    Height = Height,
                    Baseline = -m.Ascent
                };
            }

            var extra = Height - (m.Descent - m.Ascent);
            return new LineHeightResult
            {
                Height = Height,
                Baseline = -m.Ascent + extra / 2
            };
        }
    }
}
