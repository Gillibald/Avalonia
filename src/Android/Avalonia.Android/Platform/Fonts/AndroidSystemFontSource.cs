using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// The platform's own view of the installed fonts: every font file (including updated fonts
    /// outside the system font directory) and the platform's character fallback.
    /// </summary>
    internal abstract class AndroidSystemFontSource : IDisposable
    {
        /// <summary>
        /// Gets every installed font face.
        /// </summary>
        public abstract IReadOnlyList<AndroidSystemFont> GetFonts();

        /// <summary>
        /// Asks the platform which font it would draw the codepoint with.
        /// </summary>
        /// <param name="familyName">A configured family name or alias; unknown names select the default family.</param>
        /// <param name="codepoint">The codepoint.</param>
        /// <param name="weight">The requested weight.</param>
        /// <param name="italic">Whether an italic face is requested.</param>
        /// <param name="languageTags">Comma-separated language tags, or an empty string.</param>
        /// <param name="font">The font the platform chose. The platform answers even when no
        /// font covers the codepoint, so callers verify coverage.</param>
        public abstract bool TryMatch(string familyName, int codepoint, int weight, bool italic, string languageTags,
            [NotNullWhen(true)] out AndroidSystemFont? font);

        public virtual void Dispose()
        {
        }
    }

    /// <summary>
    /// One installed font face as the platform reports it.
    /// </summary>
    internal sealed record AndroidSystemFont(string Path, int Index, int Weight, bool IsItalic, string? Locale,
        IReadOnlyList<FontsXmlAxis> Axes);
}
