using System;
using Avalonia.Media;

namespace Avalonia.Documents
{
    /// <summary>
    /// Base class for inline text elements such as <see cref="Span"/> and <see cref="Run"/>/
    /// </summary>
    public abstract class Inline : TextElement
    {
        public Inline Parse(string s) => new Run(s);

        public abstract void BuildFormattedText(FormattedTextBuilder builder);

        protected FormattedTextStyleSpan GetStyleSpan(int startIndex, int length)
        {
            var brush = IsSet(ForegroundProperty) ? Foreground : null;
            Typeface typeface = null;

            if (IsSet(FontFamilyProperty) ||
                IsSet(FontSizeProperty) ||
                IsSet(FontStyleProperty) ||
                IsSet(FontWeightProperty))
            {
                typeface = new Typeface(
                    FontFamily,
                    FontSize,
                    FontStyle,
                    FontWeight);
            }

            return new FormattedTextStyleSpan(
                startIndex,
                length,
                foregroundBrush: brush,
                typeface: typeface);
        }
    }
}
