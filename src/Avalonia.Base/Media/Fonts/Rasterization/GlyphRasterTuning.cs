using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Tuning of managed glyph rasterization that tests, benchmarks and A/B comparisons change:
    /// cost factors, budgets and switches between implementations. An application binds an
    /// instance in its locator scope (<see cref="Current"/>); a drawing context reads it once,
    /// before it draws text, so a change takes effect from the next context on and ends with the
    /// scope.
    /// </summary>
    internal sealed record GlyphRasterTuning
    {
        /// <summary>The tuning of an application that binds none.</summary>
        public static GlyphRasterTuning Default { get; } = new();

        /// <summary>The tuning bound in the current locator scope, else <see cref="Default"/>.</summary>
        public static GlyphRasterTuning Current
            => AvaloniaLocator.Current.GetService<GlyphRasterTuning>() ?? Default;

        /// <summary>
        /// The tuning <paramref name="context"/> draws text with: its own when it is a
        /// <see cref="IGlyphTargetContext"/>, else <see cref="Current"/>.
        /// </summary>
        public static GlyphRasterTuning Of(IDrawingContextImpl context)
            => context is IGlyphTargetContext target ? target.RasterTuning : Current;

        /// <summary>
        /// The cost a glyph mask pixel that misses the cache adds to rasterizing an upright zoom
        /// frame on a CPU surface, in pixels of a bilinear stretch of the settled run mask through
        /// the raster backend: on x64 at 14-30 px about 11 ns per rasterized mask pixel (outline,
        /// grid fit, cache insert) against 2.2 ns per stretched pixel. Tests raise it to make
        /// rasterizing costly.
        /// </summary>
        public double ZoomRasterPixelCost { get; init; } = 5;
    }
}
