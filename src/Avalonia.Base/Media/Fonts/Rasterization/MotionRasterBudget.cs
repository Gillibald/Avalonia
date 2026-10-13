namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// How a GPU draws a frame of a run whose transform keeps changing (a zoom, an animated
    /// rotation): rasterized sharp at the frame's transform, or stretched from an earlier raster.
    /// </summary>
    internal static class MotionRasterBudget
    {
        /// <summary>
        /// The frames a thread stretches all moving text after a frame did not fit the budget,
        /// half a second at 60 Hz.
        /// </summary>
        internal const int HoldFrames = 30;

        /// <summary>The holds after which motion estimated far past the budget is tried again.</summary>
        internal const int ProbeHolds = 2;

        /// <summary>Replaces the frame's rasterization budget, in microseconds, for tests.</summary>
        internal static double? BudgetOverride { get; set; }

        /// <summary>Replaces the measured cost per glyph, in microseconds, for tests.</summary>
        internal static double? GlyphCostOverride { get; set; }
    }
}
