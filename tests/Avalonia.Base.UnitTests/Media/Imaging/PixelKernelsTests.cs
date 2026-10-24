using System;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    [CollectionDefinition(nameof(PixelKernelsTests), DisableParallelization = true)]
    public class PixelKernelsCollection
    {
    }

    // The vector switch is process-wide, so these tests run alone.
    [Collection(nameof(PixelKernelsTests))]
    public unsafe class PixelKernelsTests
    {
        // Every length up to three vectors and one more pixel, so the scalar tail runs after
        // zero, one and several vector steps.
        public static TheoryData<int> PixelCounts()
        {
            var data = new TheoryData<int>();

            for (var count = 0; count <= 3 * 4 + 1; count++)
                data.Add(count);

            data.Add(1000);

            return data;
        }

        [Theory]
        [MemberData(nameof(PixelCounts))]
        public void Copy4_Should_Match_The_Reference(int count)
        {
            foreach (var (swap, opaque) in new[] { (true, false), (false, true), (true, true), (false, false) })
            {
                var source = Random(count * 4, count);
                var expected = new byte[count * 4];

                for (var p = 0; p < count; p++)
                {
                    var s = p * 4;

                    expected[s] = swap ? source[s + 2] : source[s];
                    expected[s + 1] = source[s + 1];
                    expected[s + 2] = swap ? source[s] : source[s + 2];
                    expected[s + 3] = opaque ? (byte)255 : source[s + 3];
                }

                AssertBothPaths(expected, () =>
                {
                    var dest = new byte[count * 4];

                    PixelKernels.Copy4(source, dest, swap, opaque);
                    return dest;
                });
            }
        }

        [Theory]
        [MemberData(nameof(PixelCounts))]
        public void Expand3To4_Should_Match_The_Reference(int count)
        {
            foreach (var swap in new[] { false, true })
            {
                var source = Random(count * 3, count);
                var expected = new byte[count * 4];

                for (var p = 0; p < count; p++)
                {
                    expected[p * 4] = source[p * 3 + (swap ? 2 : 0)];
                    expected[p * 4 + 1] = source[p * 3 + 1];
                    expected[p * 4 + 2] = source[p * 3 + (swap ? 0 : 2)];
                    expected[p * 4 + 3] = 255;
                }

                AssertBothPaths(expected, () =>
                {
                    var dest = new byte[count * 4];

                    PixelKernels.Expand3To4(source, dest, swap);
                    return dest;
                });
            }
        }

        [Theory]
        [MemberData(nameof(PixelCounts))]
        public void Contract4To3_Should_Match_The_Reference_And_Write_Nothing_Past_The_Pixels(int count)
        {
            foreach (var swap in new[] { false, true })
            {
                var source = Random(count * 4, count);
                var expected = new byte[count * 3 + 8];

                Array.Fill(expected, (byte)0xEE);

                for (var p = 0; p < count; p++)
                {
                    expected[p * 3] = source[p * 4 + (swap ? 2 : 0)];
                    expected[p * 3 + 1] = source[p * 4 + 1];
                    expected[p * 3 + 2] = source[p * 4 + (swap ? 0 : 2)];
                }

                AssertBothPaths(expected, () =>
                {
                    var dest = new byte[count * 3 + 8];

                    Array.Fill(dest, (byte)0xEE);
                    PixelKernels.Contract4To3(source, dest.AsSpan(0, count * 3), swap);
                    return dest;
                });
            }
        }

        [Fact]
        public void Premultiply_Should_Match_The_Wic_Formula_For_Every_Channel_And_Alpha()
        {
            var expected = AllPairs(out var pixels);

            for (var i = 0; i < expected.Length; i += 4)
            {
                int alpha = pixels[i + 3];

                for (var c = 0; c < 3; c++)
                    expected[i + c] = (byte)((pixels[i + c] * alpha + 127) / 255);
            }

            AssertBothPaths(expected, () =>
            {
                var row = (byte[])pixels.Clone();

                PixelKernels.Premultiply(row);
                return row;
            });
        }

        [Fact]
        public void Unpremultiply_Should_Match_The_Wic_Formula_For_Every_Channel_And_Alpha()
        {
            var expected = AllPairs(out var pixels);

            for (var i = 0; i < expected.Length; i += 4)
            {
                var alpha = pixels[i + 3];

                for (var c = 0; c < 3; c++)
                {
                    expected[i + c] = alpha == 0
                        ? (byte)0
                        : (byte)Math.Min((pixels[i + c] * ((255u << 16) / alpha)) >> 16, 255u);
                }
            }

            AssertBothPaths(expected, () =>
            {
                var row = (byte[])pixels.Clone();

                PixelKernels.Unpremultiply(row);
                return row;
            });
        }

        [Theory]
        [MemberData(nameof(PixelCounts))]
        public void Alpha_Kernels_Should_Handle_Every_Length(int count)
        {
            var source = Random(count * 4, count);
            var premultiplied = (byte[])source.Clone();
            var unpremultiplied = (byte[])source.Clone();

            for (var i = 0; i < source.Length; i += 4)
            {
                int alpha = source[i + 3];
                var reciprocal = alpha == 0 ? 0u : (255u << 16) / (uint)alpha;

                for (var c = 0; c < 3; c++)
                {
                    premultiplied[i + c] = (byte)((source[i + c] * alpha + 127) / 255);
                    unpremultiplied[i + c] = (byte)Math.Min((source[i + c] * reciprocal) >> 16, 255u);
                }
            }

            AssertBothPaths(premultiplied, () =>
            {
                var row = (byte[])source.Clone();

                PixelKernels.Premultiply(row);
                return row;
            });

            AssertBothPaths(unpremultiplied, () =>
            {
                var row = (byte[])source.Clone();

                PixelKernels.Unpremultiply(row);
                return row;
            });
        }

        public static TheoryData<string, string> LayoutPairs()
        {
            var formats = new[] { "Rgba8888", "Bgra8888", "Rgb32", "Bgr32", "Rgb24", "Bgr24" };
            var data = new TheoryData<string, string>();

            foreach (var source in formats)
            {
                foreach (var dest in formats)
                    data.Add(source, dest);
            }

            return data;
        }

        // The transcoder converts these pairs directly; the staging row is the reference.
        [Theory]
        [MemberData(nameof(LayoutPairs))]
        public unsafe void Direct_Transcode_Should_Match_The_Staging_Row(string sourceName, string destName)
        {
            var sourceFormat = Format(sourceName);
            var destFormat = Format(destName);
            var size = new PixelSize(23, 3);
            var sourceStride = 23 * sourceFormat.BitsPerPixel / 8 + 5;
            var destStride = 23 * destFormat.BitsPerPixel / 8 + 3;
            var source = Random(sourceStride * size.Height, 7);
            var alphas = new[] { Avalonia.Platform.AlphaFormat.Premul, Avalonia.Platform.AlphaFormat.Unpremul, Avalonia.Platform.AlphaFormat.Opaque };

            foreach (var sourceAlpha in alphas)
            {
                foreach (var destAlpha in alphas)
                {
                    var expected = new byte[destStride * size.Height];
                    var row = new Rgba8888Pixel[size.Width];

                    fixed (byte* from = source)
                    fixed (byte* to = expected)
                    {
                        for (var y = 0; y < size.Height; y++)
                        {
                            PixelFormatTranscoder.ReadRow((IntPtr)(from + y * sourceStride), sourceFormat, row);
                            PixelFormatTranscoder.WriteRow(row, (IntPtr)(to + y * destStride), destFormat, destAlpha,
                                sourceFormat.HasAlpha ? sourceAlpha : Avalonia.Platform.AlphaFormat.Opaque);
                        }
                    }

                    // Only the pixels are compared; the stride padding is not part of the result.
                    for (var y = 0; y < size.Height; y++)
                        Array.Clear(expected, y * destStride + 23 * destFormat.BitsPerPixel / 8, destStride - 23 * destFormat.BitsPerPixel / 8);

                    AssertBothPaths(expected, () =>
                    {
                        var actual = new byte[destStride * size.Height];

                        fixed (byte* from = source)
                        fixed (byte* to = actual)
                        {
                            PixelFormatTranscoder.Transcode((IntPtr)from, size, sourceStride, sourceFormat,
                                sourceFormat.HasAlpha ? sourceAlpha : Avalonia.Platform.AlphaFormat.Opaque,
                                (IntPtr)to, destStride, destFormat, destAlpha);
                        }

                        return actual;
                    });
                }
            }
        }

        private static Avalonia.Platform.PixelFormat Format(string name) => name switch
        {
            "Rgba8888" => Avalonia.Platform.PixelFormats.Rgba8888,
            "Bgra8888" => Avalonia.Platform.PixelFormats.Bgra8888,
            "Rgb32" => Avalonia.Platform.PixelFormats.Rgb32,
            "Bgr32" => Avalonia.Platform.PixelFormats.Bgr32,
            "Rgb24" => Avalonia.Platform.PixelFormats.Rgb24,
            _ => Avalonia.Platform.PixelFormats.Bgr24,
        };

        // Pixels (c, 255 - c, c, a) for every channel value c and alpha a.
        private static byte[] AllPairs(out byte[] pixels)
        {
            pixels = new byte[256 * 256 * 4];

            for (var a = 0; a < 256; a++)
            {
                for (var c = 0; c < 256; c++)
                {
                    var i = (a * 256 + c) * 4;

                    pixels[i] = (byte)c;
                    pixels[i + 1] = (byte)(255 - c);
                    pixels[i + 2] = (byte)c;
                    pixels[i + 3] = (byte)a;
                }
            }

            return (byte[])pixels.Clone();
        }

        private static byte[] Random(int length, int seed)
        {
            var bytes = new byte[length];

            new Random(seed).NextBytes(bytes);

            return bytes;
        }

        private static void AssertBothPaths(byte[] expected, Func<byte[]> run)
        {
            var previous = PixelKernels.UseVectors;

            try
            {
                PixelKernels.UseVectors = false;
                Assert.Equal(expected, run());

                PixelKernels.UseVectors = true;
                Assert.Equal(expected, run());
            }
            finally
            {
                PixelKernels.UseVectors = previous;
            }
        }
    }
}
