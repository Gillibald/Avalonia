namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Uses the actual ascent/descent/line-gap from all runs on the line.
    /// Each line may have a different height depending on its content.
    /// </summary>
    /// <remarks>
    /// This is the default behavior when no <see cref="LineHeightStrategy"/> is specified
    /// and <c>LineHeight</c> is <c>NaN</c>.
    /// Best for text editors, documents, and rich text.
    /// </remarks>
    public sealed class NaturalLineHeightStrategy : LineHeightStrategy
    {
        /// <summary>
        /// Gets the shared singleton instance.
        /// </summary>
        public static NaturalLineHeightStrategy Instance { get; } = new();

        /// <inheritdoc/>
        public override LineHeightResult Compute(in LineNaturalMetrics m)
        {
            return new LineHeightResult
            {
                Height = m.NaturalHeight,
                Baseline = m.NaturalBaseline
            };
        }
    }
}
