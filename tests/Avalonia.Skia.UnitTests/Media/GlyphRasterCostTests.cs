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

        [Fact]
        public void An_Unhinted_Cost_Reports_The_Grid_Fitted_Cost_Until_It_Has_Samples_Of_Its_Own()
        {
            // Motion frames rasterize unhinted, which costs less than the grid-fitted builds of
            // static text: before the first unhinted build, their cost is estimated from above.
            var gridFitted = new GlyphRasterCost();
            var unhinted = new GlyphRasterCost(gridFitted);

            for (var i = 0; i < 100; i++)
            {
                gridFitted.Record(Size, Ticks(Cold));
            }

            Assert.Equal(gridFitted.PerGlyph(Size), unhinted.PerGlyph(Size));

            unhinted.Record(Size, Ticks(Warm));

            Assert.InRange(unhinted.PerGlyph(Size), Warm * 0.9, Warm * 1.1);
            Assert.InRange(gridFitted.PerGlyph(Size), Cold * 0.9, Cold * 1.1);
        }

        [Fact]
        public void A_Typeface_Learns_The_Cost_Of_Grid_Fitted_And_Unhinted_Masks_Apart()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var scratch = new GlyphPathBuilder();
            var gridFitted = typeface.MaskCache.RasterCost;
            var unhinted = typeface.MaskCache.UnhintedRasterCost;
            ushort scaleQ = 0;

            void Build(bool gridFit)
            {
                // A new size per build, so every build rasterizes.
                scaleQ++;

                foreach (var c in "Hamburgefonstiv")
                {
                    GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(typeface.CharacterToGlyphMap[c],
                        (ushort)(GlyphMaskKey.ScaleQuantum * 14 + scaleQ), 0, GlyphMaskMode.Antialiased, gridFit));
                }
            }

            Build(gridFit: true);

            var gridFittedCost = gridFitted.PerGlyph(Size);

            for (var i = 0; i < 4; i++)
            {
                Build(gridFit: false);
            }

            Assert.Equal(gridFittedCost, gridFitted.PerGlyph(Size));

            var unhintedCost = unhinted.PerGlyph(Size);

            for (var i = 0; i < 4; i++)
            {
                Build(gridFit: true);
            }

            Assert.Equal(unhintedCost, unhinted.PerGlyph(Size));
            Assert.NotEqual(gridFittedCost, gridFitted.PerGlyph(Size));
        }
    }
}
