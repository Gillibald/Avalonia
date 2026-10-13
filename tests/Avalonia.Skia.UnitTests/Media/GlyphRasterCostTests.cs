using System.Diagnostics;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A typeface's learned cost per glyph follows the rasterizations that run warm: the first
    /// rasterizations of a process run on code that is still compiling and take several times
    /// longer, and a rasterization interrupted by a collection or preemption takes longer still,
    /// but neither keeps the estimate high once warm rasterizations come in.
    /// </summary>
    public class GlyphRasterCostTests
    {
        private const double Cold = 12;
        private const double Warm = 1.5;
        private const double Size = GlyphRasterCost.ReferencePixelsPerEm;

        private static long Ticks(double microseconds) => (long)(microseconds * Stopwatch.Frequency / 1e6);

        [Fact]
        public void Warm_Rasterizations_After_Cold_Ones_Bring_The_Estimate_Down_Within_A_Frame_Of_Text()
        {
            var cost = new GlyphRasterCost();

            for (var i = 0; i < 400; i++)
            {
                cost.Record(Size, Ticks(Cold));
            }

            // A paragraph's worth of warm rasterizations.
            for (var i = 0; i < 60; i++)
            {
                cost.Record(Size, Ticks(Warm));
            }

            Assert.InRange(cost.PerGlyph(Size), Warm * 0.9, Warm * 1.1);
        }

        [Fact]
        public void A_Single_Slow_Rasterization_Barely_Raises_A_Warm_Estimate()
        {
            var cost = new GlyphRasterCost();

            for (var i = 0; i < 100; i++)
            {
                cost.Record(Size, Ticks(Warm));
            }

            cost.Record(Size, Ticks(Warm * 100));

            Assert.InRange(cost.PerGlyph(Size), Warm * 0.9, Warm * 1.1);
        }
    }
}
