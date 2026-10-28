using System;
using System.Globalization;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting.Unicode;

namespace Avalonia.Media.TextFormatting
{
    /// <summary>
    /// The fallback face the itemizer tries before it asks the font manager, so that consecutive runs
    /// that need a fallback stay on one face. It only holds a face the font manager chose for an
    /// earlier run, never a span's own typeface: a family one span requested is no fallback for the
    /// spans after it. The face only answers text of the (script, culture) bucket it was chosen for,
    /// the key the font collection replays its own fallback answers by.
    /// </summary>
    internal readonly struct FallbackTypefaceReuse
    {
        public FallbackTypefaceReuse(Typeface typeface, GlyphTypeface glyphTypeface, Script script,
            CultureInfo? culture)
        {
            Typeface = typeface;
            GlyphTypeface = glyphTypeface;
            Script = script;
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
        /// The script of the bucket the face was chosen for, as
        /// <see cref="FontFallbackScriptHints.RefineWithCulture"/> refines it for <see cref="Culture"/>.
        /// </summary>
        public Script Script { get; }

        /// <summary>
        /// The culture of the run the face was chosen for.
        /// </summary>
        public CultureInfo? Culture { get; }

        /// <summary>
        /// Returns whether <paramref name="codepoint"/> in <paramref name="culture"/> belongs to the
        /// bucket the face was chosen for. In ja-JP Han and Hiragana share a bucket, so Japanese text
        /// keeps one face; in zh-CN Han and kana do not, so kana after Han are left to the font
        /// manager and its writing-system rules.
        /// </summary>
        public bool IsBucketOf(Codepoint codepoint, CultureInfo? culture)
            => GlyphTypeface is not null
               && FontFallbackScriptHints.RefineWithCulture(codepoint, culture) == Script
               && string.Equals(Culture?.Name, culture?.Name, StringComparison.Ordinal);
    }
}
