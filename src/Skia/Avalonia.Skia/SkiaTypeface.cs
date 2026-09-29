using System;
using Avalonia.Media;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia's render typeface: the SKTypeface plus an SKFont factory applying a glyph typeface's
    /// algorithmic style simulations. Produced by <see cref="PlatformRenderInterface.CreateTypeface"/>
    /// from the glyph typeface's font data and consumed by the glyph run and geometry paths. The
    /// simulations are a draw-time parameter, so every simulated variant of a face shares one
    /// render typeface.
    /// </summary>
    internal class SkiaTypeface : IPlatformTypeface
    {
        public SkiaTypeface(SKTypeface typeface)
        {
            SKTypeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
        }

        public SKTypeface SKTypeface { get; }

        public string FamilyName => SKTypeface.FamilyName;

        public SKFont CreateSKFont(float size, FontSimulations simulations)
        {
            var skewX = (simulations & FontSimulations.Oblique) != 0 ? -FontSimulationConstants.ObliqueSlant : 0.0f;

            return new(SKTypeface, size, skewX: skewX)
            {
                LinearMetrics = true,
                Embolden = (simulations & FontSimulations.Bold) != 0
            };
        }

        public void Dispose()
        {
            SKTypeface.Dispose();
        }
    }
}
