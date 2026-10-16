using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Tuning of managed glyph rasterization that tests, benchmarks and A/B comparisons change:
    /// cost factors, budgets and switches between implementations. It belongs to the application's
    /// <see cref="GlyphRasterizationContext"/> (<see cref="GlyphRasterizationContext.Tuning"/>); a
    /// drawing context reads it once, before it draws text, so a change takes effect from the next
    /// context on and ends with the application.
    /// </summary>
    internal sealed record GlyphRasterTuning
    {
        /// <summary>The tuning of an application that binds none.</summary>
        public static GlyphRasterTuning Default { get; } = new();

        /// <summary>The tuning of the current application's context, else <see cref="Default"/>.</summary>
        public static GlyphRasterTuning Current => GlyphRasterizationContext.Current?.Tuning ?? Default;

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

        /// <summary>
        /// The most time a frame may spend rasterizing motion frames sharp, in microseconds;
        /// <c>null</c> keeps <see cref="MotionRasterBudget.FrameBudgetMicroseconds"/>.
        /// </summary>
        public double? MotionBudgetMicroseconds { get; init; }

        /// <summary>
        /// The cost per glyph the motion budget assumes, in microseconds: a run is estimated at its
        /// glyph count times it, whatever its masks find cached, and a frame that rasterizes is
        /// charged its estimate rather than the time it took, so decisions depend neither on the
        /// machine nor on what was cached before. <c>null</c> measures.
        /// </summary>
        public double? MotionGlyphCostMicroseconds { get; init; }

        /// <summary>
        /// The interval between frames the motion budget assumes, in microseconds; <c>null</c>
        /// assumes a 120 Hz display.
        /// </summary>
        public double? MotionFrameIntervalMicroseconds { get; init; }

        /// <summary>
        /// The time frames spend outside text that the motion budget assumes, in microseconds;
        /// <c>null</c> measures it.
        /// </summary>
        public double? MotionNonTextMicroseconds { get; init; }

        /// <summary>
        /// Whether colour glyphs replay their cached recordings where the context supports
        /// recordings; off draws every colour glyph from its live drawing. For A/B comparisons.
        /// </summary>
        public bool UseColorRecordings { get; init; } = true;

        /// <summary>
        /// Whether managed rasterization draws COLR v1 glyphs of upright runs from colour masks
        /// (each paint graph rasterized once per scale and phase); off draws them as vectors on
        /// every draw. For A/B comparisons.
        /// </summary>
        public bool UseColorMasks { get; init; } = true;

        /// <summary>
        /// Whether hardware GPU contexts draw the colour glyph content of managed runs from colour
        /// atlas pages, batched across runs; off draws upright runs from their run masks and cuts
        /// transformed runs at their v1 glyphs. For A/B comparisons.
        /// </summary>
        public bool UseColorAtlas { get; init; } = true;

        /// <summary>
        /// The instruction set direct blends into raster surfaces use; <c>null</c> takes the best
        /// the machine supports (<see cref="GlyphMaskBlitter.DetectedPath"/>). Tests and benchmarks
        /// lower it to cover every path.
        /// </summary>
        public GlyphBlitPath? BlitPath { get; init; }
    }
}
