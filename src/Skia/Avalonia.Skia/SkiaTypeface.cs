using System;
using Avalonia.Media;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia's render typeface: the SKTypeface plus an SKFont factory applying the glyph
    /// typeface's algorithmic style simulations. Produced by
    /// <see cref="PlatformRenderInterface.CreateTypeface"/> from the glyph typeface's font data
    /// and consumed by the glyph run and geometry paths.
    /// </summary>
    internal class SkiaTypeface : IPlatformTypeface
    {
        public SkiaTypeface(SKTypeface typeface, FontSimulations fontSimulations)
        {
            SKTypeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
            FontSimulations = fontSimulations;
        }

        public SKTypeface SKTypeface { get; }

        public FontSimulations FontSimulations { get; }

        public string FamilyName => SKTypeface.FamilyName;

        public SKFont CreateSKFont(float size)
        {
            return new(SKTypeface, size, skewX: (FontSimulations & FontSimulations.Oblique) != 0 ? -0.3f : 0.0f)
            {
                LinearMetrics = true,
                Embolden = (FontSimulations & FontSimulations.Bold) != 0
            };
        }

        public void Dispose()
        {
            SKTypeface.Dispose();
        }
    }
}
