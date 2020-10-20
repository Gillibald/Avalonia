using System.Collections.Generic;
using Avalonia.Utilities;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// Represents a metric for a <see cref="TextLine"/> objects,
    /// that holds information about ascent, descent, line gap, size and origin of the text line.
    /// </summary>
    public readonly struct TextLineMetrics
    {
        public TextLineMetrics(TextRange textRange, double start, double height, double width, double widthIncludingTrailingWhitespace,
            double textBaseline, bool hasOverflowed)
        {
            TextRange = textRange;
            Start = start;
            Height = height;
            Width = width;
            WidthIncludingTrailingWhitespace = widthIncludingTrailingWhitespace;
            TextBaseline = textBaseline;
            HasOverflowed = hasOverflowed;
        }

        /// <summary>
        /// Gets the text range that is covered by the text line.
        /// </summary>
        /// <value>
        /// The text range that is covered by the text line.
        /// </value>
        public TextRange TextRange { get; }

        public double Start { get; }

        public double Height { get; }

        public double Width { get; }

        public double WidthIncludingTrailingWhitespace { get; }

        public bool HasOverflowed { get; }

        /// <summary>
        /// Gets the distance from the top to the baseline of the line of text.
        /// </summary>
        public double TextBaseline { get; }

        /// <summary>
        /// Creates the text line metrics.
        /// </summary>
        /// <param name="textRuns">The text runs.</param>
        /// <param name="textRange"></param>
        /// <param name="paragraphWidth">The paragraph width.</param>
        /// <param name="paragraphProperties">The text alignment.</param>
        /// <returns></returns>
        public static TextLineMetrics Create(IEnumerable<TextRun> textRuns, TextRange textRange, double paragraphWidth,
            TextParagraphProperties paragraphProperties)
        {
            var lineWidth = 0.0;
            var ascent = 0.0;
            var descent = 0.0;
            var lineGap = 0.0;

            var start = paragraphProperties.Indent;

            foreach (var textRun in textRuns)
            {
                var shapedRun = (ShapedTextCharacters)textRun;

                var fontMetrics =
                    new FontMetrics(shapedRun.Properties.Typeface, shapedRun.Properties.FontRenderingEmSize);

                lineWidth += shapedRun.Size.Width;

                if (ascent > fontMetrics.Ascent)
                {
                    ascent = fontMetrics.Ascent;
                }

                if (descent < fontMetrics.Descent)
                {
                    descent = fontMetrics.Descent;
                }

                if (lineGap < fontMetrics.LineGap)
                {
                    lineGap = fontMetrics.LineGap;
                }
            }

            var height = double.IsNaN(paragraphProperties.LineHeight) ||
                         MathUtilities.IsZero(paragraphProperties.LineHeight) ?
                descent - ascent + lineGap :
                paragraphProperties.LineHeight;

            return new TextLineMetrics(textRange, start, height, lineWidth, lineWidth, -ascent, lineWidth > paragraphWidth);
        }
    }
}
