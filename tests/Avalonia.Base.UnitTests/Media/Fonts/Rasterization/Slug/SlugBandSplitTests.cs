using System;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugBandSplitTests
    {
        private const int BarCount = 16;
        private const double BarPitch = 1.0 / 16;
        private const double BarWidth = 1.0 / 64;

        /// <summary>
        /// Sixteen separated full-height bars. Every vertical edge crosses every horizontal band,
        /// so each horizontal band list holds all 32 of them, and each bar's top and bottom edge
        /// share its vertical band. The bars sit at multiples of 1/16 em with the x midpoint
        /// (61/128 em) in the gap between bars 7 and 8, more than the split margin away from
        /// both, so no edge lands on both sides of the split.
        /// </summary>
        private static void DrawBars(SlugContourSink sink)
        {
            for (var i = 0; i < BarCount; i++)
            {
                var left = i * BarPitch;
                var right = left + BarWidth;

                sink.BeginFigure(new Point(left, 0));
                sink.LineTo(new Point(right, 0));
                sink.LineTo(new Point(right, 1));
                sink.LineTo(new Point(left, 1));
                sink.EndFigure(true);
            }
        }

        private static (SlugTexelSerializer Serializer, SlugGlyphData Data, SlugGlyphPlacement Placement)
            Prepare(Action<SlugContourSink> draw)
        {
            var sink = new SlugContourSink();

            draw(sink);

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            return (serializer, data!, placement);
        }

        [Fact]
        public void Split_Bands_Halve_The_Loop_Bound_Of_Separated_Bars()
        {
            var (_, _, placement) = Prepare(DrawBars);

            Assert.Equal(2 * BarCount, placement.LongestHorizontalList);
            Assert.Equal(2, placement.LongestVerticalList);

            // At 64 px per em the half-pixel reach fits inside the margin, so each pixel walks
            // only the edges on its side of the midpoint: the 16 edges of bars 0-7 or of bars
            // 8-15, with nothing shared. Vertically each band holds one bar's bottom edge
            // (below the y midpoint) and its top edge (above it).
            Assert.Equal(BarCount, placement.GetHorizontalLoopBound(64));
            Assert.Equal(1, placement.GetVerticalLoopBound(64));

            // Below the enable threshold the whole list is walked.
            Assert.Equal(2 * BarCount, placement.GetHorizontalLoopBound(16));
            Assert.Equal(2, placement.GetVerticalLoopBound(16));
        }
    }
}
