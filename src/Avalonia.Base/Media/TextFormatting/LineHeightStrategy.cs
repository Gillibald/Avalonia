namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Natural metrics of a text line, as measured from all shaped/drawable runs.
    /// </summary>
    public readonly record struct LineNaturalMetrics
    {
        /// <summary>
        /// Gets the ascent of the line (negative value, above baseline).
        /// This is the maximum ascent across all runs on the line.
        /// </summary>
        public double Ascent { get; init; }

        /// <summary>
        /// Gets the descent of the line (positive value, below baseline).
        /// This is the maximum descent across all runs on the line.
        /// </summary>
        public double Descent { get; init; }

        /// <summary>
        /// Gets the line gap.
        /// This is the maximum line gap across all runs on the line.
        /// </summary>
        public double LineGap { get; init; }

        /// <summary>
        /// Gets the natural height of the line (Descent - Ascent + LineGap).
        /// </summary>
        public double NaturalHeight { get; init; }

        /// <summary>
        /// Gets the natural baseline offset from the top of the line (-Ascent + LineGap * 0.5).
        /// </summary>
        public double NaturalBaseline { get; init; }

        /// <summary>
        /// Gets the ascent from the paragraph's default font only (ignoring fallback runs).
        /// </summary>
        public double DefaultFontAscent { get; init; }

        /// <summary>
        /// Gets the descent from the paragraph's default font only.
        /// </summary>
        public double DefaultFontDescent { get; init; }

        /// <summary>
        /// Gets the line gap from the paragraph's default font only.
        /// </summary>
        public double DefaultFontLineGap { get; init; }

        /// <summary>
        /// Gets the ink bounds extent (actual drawn pixel height).
        /// </summary>
        public double InkExtent { get; init; }
    }

    /// <summary>
    /// The result of a <see cref="LineHeightStrategy"/> computation.
    /// </summary>
    public readonly record struct LineHeightResult
    {
        /// <summary>
        /// Gets the effective line height.
        /// </summary>
        public double Height { get; init; }

        /// <summary>
        /// Gets the effective baseline offset from the top of the line.
        /// </summary>
        public double Baseline { get; init; }
    }

    /// <summary>
    /// Determines how line height is computed for a text line given its natural metrics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="LineHeightStrategy"/> separates the <em>policy</em> of how line heights are determined
    /// from the <em>measurement</em> of text runs. The text engine measures each line's natural metrics
    /// (ascent, descent, line gap, ink extent) and passes them to the strategy, which returns the
    /// effective height and baseline for layout.
    /// </para>
    /// <para>
    /// Built-in strategies include:
    /// <list type="bullet">
    ///     <item><see cref="NaturalLineHeightStrategy"/> — uses the actual metrics from all runs (default behavior).</item>
    ///     <item><see cref="FixedLineHeightStrategy"/> — uses a fixed height (DOCX Exactly / RTF negative \sl).</item>
    ///     <item><see cref="AtLeastLineHeightStrategy"/> — uses a minimum floor (DOCX AtLeast / RTF positive \sl).</item>
    ///     <item><see cref="MultipleLineHeightStrategy"/> — proportional multiplier (DOCX Auto / RTF \slmult1).</item>
    ///     <item><see cref="UniformLineHeightStrategy"/> — CSS-like, based on default font only.</item>
    ///     <item><see cref="ClampedLineHeightStrategy"/> — natural with a maximum cap, never clipping ink.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public abstract class LineHeightStrategy
    {
        /// <summary>
        /// Computes the effective line height and baseline given the natural metrics of a text line.
        /// </summary>
        /// <param name="naturalMetrics">The natural metrics computed from all runs on the line.</param>
        /// <returns>The effective height and baseline for the line.</returns>
        public abstract LineHeightResult Compute(in LineNaturalMetrics naturalMetrics);
    }
}
