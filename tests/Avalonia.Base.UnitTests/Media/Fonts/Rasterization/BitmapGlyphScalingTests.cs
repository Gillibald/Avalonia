using System;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Strike bitmaps composed at another size than their strike: a smaller size averages the
    /// source area each destination pixel covers, a larger size interpolates between the four
    /// nearest source texels, both on premultiplied texels; the strike's own size copies it.
    /// </summary>
    public class BitmapGlyphScalingTests
    {
        private const int SourceWidth = 48;
        private const int SourceHeight = 40;

        [Theory]
        [InlineData(42, 35)] // 7/8
        [InlineData(24, 20)] // 1/2
        [InlineData(16, 13)] // about 1/3
        [InlineData(5, 3)]
        public void A_Smaller_Size_Averages_The_Source_Area_Each_Pixel_Covers(int width, int height)
        {
            var source = CreateSource(SourceWidth, SourceHeight);
            var actual = Compose(source, width, height);

            AssertWithinOneLevel(AreaAverage(source, width, height), actual, width, height);
        }

        [Theory]
        [InlineData(72, 60)] // 1.5
        [InlineData(97, 41)]
        public void A_Larger_Size_Interpolates_Between_The_Nearest_Texels(int width, int height)
        {
            var source = CreateSource(SourceWidth, SourceHeight);
            var actual = Compose(source, width, height);

            AssertWithinOneLevel(Bilinear(source, width, height), actual, width, height);
        }

        [Fact]
        public void The_Strike_Size_Copies_The_Bitmap()
        {
            var source = CreateSource(SourceWidth, SourceHeight);

            Assert.Equal(source.Bgra, Compose(source, SourceWidth, SourceHeight));
        }

        [Theory]
        [InlineData(16, 16)]
        [InlineData(31, 31)]
        [InlineData(70, 70)]
        public void Edges_Keep_Their_Colour(int width, int height)
        {
            // An opaque red square on a transparent ground: wherever the scaled square is partly
            // covered, the premultiplied texel must stay pure red at that coverage, not darken.
            var bgra = new byte[48 * 48 * 4];

            // Edges off the texel grid of every tested scale, so some pixels are partly covered.
            for (var y = 13; y < 35; y++)
            {
                for (var x = 13; x < 35; x++)
                {
                    bgra[(y * 48 + x) * 4 + 2] = 255;
                    bgra[(y * 48 + x) * 4 + 3] = 255;
                }
            }

            var actual = Compose(new DecodedGlyphBitmap(bgra, 48, 48), width, height);
            var partial = 0;

            for (var i = 0; i < actual.Length; i += 4)
            {
                var alpha = actual[i + 3];

                Assert.Equal(0, actual[i]);
                Assert.Equal(0, actual[i + 1]);
                Assert.True(Math.Abs(actual[i + 2] - alpha) <= 1, $"texel {i / 4}: red {actual[i + 2]} at alpha {alpha}");

                if (alpha is > 0 and < 255)
                {
                    partial++;
                }
            }

            Assert.True(partial > 0, "no edge was partly covered");
        }

        [Fact]
        public void A_Scaled_Bitmap_Is_Placed_And_Clipped_Like_The_Strike_Size()
        {
            var source = CreateSource(SourceWidth, SourceHeight);
            var expected = AreaAverage(source, 24, 20);
            const int bufferWidth = 30;
            const int bufferHeight = 18;
            var buffer = new byte[bufferWidth * bufferHeight * 4];

            RunMaskComposer.ComposeBitmap(source, 10, -4, 24, 20, buffer, bufferWidth, bufferHeight);

            for (var y = 0; y < bufferHeight; y++)
            {
                for (var x = 0; x < bufferWidth; x++)
                {
                    var sx = x - 10;
                    var sy = y + 4;

                    for (var c = 0; c < 4; c++)
                    {
                        double want = sx >= 0 && sx < 24 && sy < 20 ? expected[(sy * 24 + sx) * 4 + c] : 0;

                        Assert.True(Math.Abs(buffer[(y * bufferWidth + x) * 4 + c] - want) <= 1,
                            $"({x},{y}) channel {c}: {buffer[(y * bufferWidth + x) * 4 + c]} vs {want:0.##}");
                    }
                }
            }
        }

        /// <summary>A premultiplied BGRA bitmap with colour and alpha gradients and a checker, every texel valid.</summary>
        internal static DecodedGlyphBitmap CreateSource(int width, int height)
        {
            var bgra = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = ((x / 3 + y / 3) & 1) == 0 ? 255 : (x * 255 / (width - 1) + y * 97) % 256;
                    var i = (y * width + x) * 4;

                    bgra[i] = (byte)(alpha * ((x * 7 + y * 3) % 256) / 255);
                    bgra[i + 1] = (byte)(alpha * (y * 255 / (height - 1)) / 255);
                    bgra[i + 2] = (byte)(alpha * (255 - x * 255 / (width - 1)) / 255);
                    bgra[i + 3] = (byte)alpha;
                }
            }

            return new DecodedGlyphBitmap(bgra, width, height);
        }

        internal static byte[] Compose(in DecodedGlyphBitmap source, int width, int height)
        {
            var buffer = new byte[width * height * 4];

            RunMaskComposer.ComposeBitmap(source, 0, 0, width, height, buffer, width, height);

            return buffer;
        }

        /// <summary>
        /// The mean of the source over the rectangle a destination pixel covers when the bitmap's
        /// width and height are stretched onto the destination's, each texel weighted by its overlap.
        /// </summary>
        private static double[] AreaAverage(in DecodedGlyphBitmap source, int width, int height)
        {
            var result = new double[width * height * 4];
            var scaleX = (double)source.Width / width;
            var scaleY = (double)source.Height / height;

            for (var y = 0; y < height; y++)
            {
                var top = y * scaleY;
                var bottom = (y + 1) * scaleY;

                for (var x = 0; x < width; x++)
                {
                    var left = x * scaleX;
                    var right = (x + 1) * scaleX;

                    for (var sy = (int)Math.Floor(top); sy < Math.Min(source.Height, (int)Math.Ceiling(bottom)); sy++)
                    {
                        var wy = Math.Min(bottom, sy + 1) - Math.Max(top, sy);

                        for (var sx = (int)Math.Floor(left); sx < Math.Min(source.Width, (int)Math.Ceiling(right)); sx++)
                        {
                            var w = wy * (Math.Min(right, sx + 1) - Math.Max(left, sx)) / (scaleX * scaleY);

                            for (var c = 0; c < 4; c++)
                            {
                                result[(y * width + x) * 4 + c] += w * source.Bgra[(sy * source.Width + sx) * 4 + c];
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Linear interpolation between the two nearest texel centres on each axis, the
        /// destination pixel centre mapped onto the source and held inside its outermost centres.
        /// </summary>
        private static double[] Bilinear(DecodedGlyphBitmap source, int width, int height)
        {
            var result = new double[width * height * 4];
            var texels = source.Bgra;
            var stride = source.Width;

            for (var y = 0; y < height; y++)
            {
                Tap(y, height, source.Height, out var y0, out var y1, out var fy);

                for (var x = 0; x < width; x++)
                {
                    Tap(x, width, source.Width, out var x0, out var x1, out var fx);

                    for (var c = 0; c < 4; c++)
                    {
                        double At(int sx, int sy) => texels[(sy * stride + sx) * 4 + c];

                        result[(y * width + x) * 4 + c] =
                            (1 - fy) * ((1 - fx) * At(x0, y0) + fx * At(x1, y0)) +
                            fy * ((1 - fx) * At(x0, y1) + fx * At(x1, y1));
                    }
                }
            }

            return result;

            static void Tap(int d, int destination, int sourceLength, out int i0, out int i1, out double f)
            {
                var p = Math.Clamp((d + 0.5) * sourceLength / destination - 0.5, 0, sourceLength - 1);

                i0 = (int)Math.Floor(p);
                i1 = Math.Min(i0 + 1, sourceLength - 1);
                f = p - i0;
            }
        }

        private static void AssertWithinOneLevel(double[] expected, byte[] actual, int width, int height)
        {
            var worst = 0.0;
            var at = -1;

            for (var i = 0; i < expected.Length; i++)
            {
                var difference = Math.Abs(actual[i] - expected[i]);

                if (difference > worst)
                {
                    worst = difference;
                    at = i;
                }
            }

            Assert.True(worst <= 1,
                $"{width}x{height}: texel {at / 4} channel {at % 4} is {actual[Math.Max(at, 0)]}, expected {expected[Math.Max(at, 0)]:0.##}");
        }
    }
}
