using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The tests that replace the motion raster budget's process-wide inputs (its cap, the cost
    /// per glyph, the time outside text, the frame interval) or the zoom's raster cost. They run
    /// alone: a test drawing moving text in parallel would see another test's values.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class MotionBudgetCollection
    {
        public const string Name = "Motion budget";
    }
}
