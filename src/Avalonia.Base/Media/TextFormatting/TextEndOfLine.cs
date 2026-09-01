using System;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// A text run that indicates the end of a line.
    /// </summary>
    public class TextEndOfLine : TextRun
    {
        public TextEndOfLine(int textSourceLength = DefaultTextSourceLength)
        {
            Length = textSourceLength;
        }

        /// <summary>
        /// Constructs a run for the line break characters a line ends with. The run carries them so
        /// they keep their place in the text source without being handed to the shaper.
        /// </summary>
        /// <param name="text">The line break characters.</param>
        /// <param name="textRunProperties">The properties of the text the break was split from.</param>
        public TextEndOfLine(ReadOnlyMemory<char> text, TextRunProperties? textRunProperties = null)
        {
            Text = text;
            Length = text.Length;
            Properties = textRunProperties;
        }

        /// <inheritdoc />
        public override int Length { get; }

        /// <inheritdoc />
        public override ReadOnlyMemory<char> Text { get; }

        /// <inheritdoc />
        public override TextRunProperties? Properties { get; }
    }
}
