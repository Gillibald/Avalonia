using System;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class GlyphMaskInkBoundsTests
    {
        [Fact]
        public void Ink_Bounds_Are_The_Smallest_Rectangle_Holding_Every_Covered_Pixel()
        {
            var random = new Random(77);

            for (var trial = 0; trial < 500; trial++)
            {
                var width = random.Next(1, 40);
                var height = random.Next(1, 40);
                var alpha = new byte[width * height];
                var covered = random.Next(0, width * height / 3 + 2);

                for (var i = 0; i < covered; i++)
                {
                    alpha[random.Next(alpha.Length)] = (byte)random.Next(1, 256);
                }

                var mask = new GlyphMask(alpha, width, height, -3, -5);

                mask.GetInkBounds(out var x, out var y, out var inkWidth, out var inkHeight);

                var expected = Measure(alpha, width, height);

                Assert.Equal(expected, (x, y, inkWidth, inkHeight));
            }
        }

        [Fact]
        public void A_Mask_Without_Coverage_Has_No_Ink()
        {
            var mask = new GlyphMask(new byte[12 * 7], 12, 7, 0, 0);

            mask.GetInkBounds(out _, out _, out var width, out var height);

            Assert.Equal(0, width);
            Assert.Equal(0, height);
        }

        [Fact]
        public void A_Mask_Covered_At_Its_Corners_Keeps_Its_Full_Extent()
        {
            var alpha = new byte[9 * 6];

            alpha[0] = 1;
            alpha[^1] = 200;

            new GlyphMask(alpha, 9, 6, 0, 0).GetInkBounds(out var x, out var y, out var width, out var height);

            Assert.Equal((0, 0, 9, 6), (x, y, width, height));
        }

        [Fact]
        public void A_Multi_Channel_Mask_Reports_Its_Full_Extent()
        {
            var mask = new GlyphMask(new byte[5 * 4 * 3], 5, 4, 0, 0, channels: 3);

            mask.GetInkBounds(out var x, out var y, out var width, out var height);

            Assert.Equal((0, 0, 5, 4), (x, y, width, height));
        }

        [Fact]
        public void A_Mask_Over_A_Longer_Buffer_Measures_Only_Its_Own_Pixels()
        {
            var buffer = new byte[64];

            buffer.AsSpan().Fill(255);
            buffer.AsSpan(0, 4 * 3).Clear();
            buffer[1 * 4 + 2] = 9;

            var mask = GlyphMask.CreateOverBuffer(buffer, 4, 3, 0, 0);

            mask.GetInkBounds(out var x, out var y, out var width, out var height);

            Assert.Equal((2, 1, 1, 1), (x, y, width, height));
        }

        private static (int, int, int, int) Measure(byte[] alpha, int width, int height)
        {
            int left = width, top = height, right = -1, bottom = -1;

            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    if (alpha[row * width + column] == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, column);
                    right = Math.Max(right, column);
                    top = Math.Min(top, row);
                    bottom = Math.Max(bottom, row);
                }
            }

            return right < 0 ? (0, 0, 0, 0) : (left, top, right - left + 1, bottom - top + 1);
        }
    }
}
