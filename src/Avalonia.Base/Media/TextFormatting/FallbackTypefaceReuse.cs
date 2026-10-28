using System.Globalization;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// The fallback face the itemizer tries before it asks the font manager, so that consecutive runs
    /// that need a fallback stay on one face. It only holds a face the font manager chose for an
    /// earlier run, never a span's own typeface: a family one span requested is no fallback for the
    /// spans after it.
    /// </summary>
    internal readonly struct FallbackTypefaceReuse
    {
        public FallbackTypefaceReuse(Typeface typeface, GlyphTypeface glyphTypeface, CultureInfo? culture)
        {
            Typeface = typeface;
            GlyphTypeface = glyphTypeface;
            Culture = culture;
        }

        /// <summary>
        /// The typeface of the run the face was chosen for. Only its family carries over; a run that
        /// reuses it keeps its own style, weight and stretch.
        /// </summary>
        public Typeface Typeface { get; }

        /// <summary>
        /// The glyph typeface of <see cref="Typeface"/>, or <c>null</c> when there is nothing to reuse.
        /// </summary>
        public GlyphTypeface? GlyphTypeface { get; }

        /// <summary>
        /// The culture of the run the face was chosen for.
        /// </summary>
        public CultureInfo? Culture { get; }
    }
}
