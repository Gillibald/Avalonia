using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImagingDemo
{
    /// <summary>
    /// Procedurally generated source images. Each is built straight into a
    /// <see cref="PixelBuffer"/> with <see cref="PixelBuffer.Create"/>, so the sample
    /// has content to work on without shipping any binary assets - and it exercises the
    /// managed pixel-authoring entry point of the new infrastructure.
    /// </summary>
    internal static class SampleImages
    {
        /// <summary>
        /// A diagonal RGB gradient - fully opaque, useful for size/format/transform demos.
        /// </summary>
        public static PixelBuffer CreateGradient(int width = 360, int height = 240)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = y * stride + x * 4;

                    // Rgba8888 is R, G, B, A in memory order.
                    pixels[o + 0] = (byte)(255 * x / (width - 1));
                    pixels[o + 1] = (byte)(255 * y / (height - 1));
                    pixels[o + 2] = (byte)(255 - 255 * x / (width - 1));
                    pixels[o + 3] = 255;
                }
            }

            return PixelBuffer.Create(pixels, new PixelSize(width, height), stride,
                PixelFormats.Rgba8888, AlphaFormat.Unpremul, new Vector(96, 96));
        }

        /// <summary>
        /// A two-tone checkerboard where the light cells are semi-transparent, so alpha
        /// handling through decode/encode is visible.
        /// </summary>
        public static PixelBuffer CreateCheckerboard(int width = 360, int height = 240, int cell = 30)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = y * stride + x * 4;
                    var solid = ((x / cell) + (y / cell)) % 2 == 0;

                    if (solid)
                    {
                        pixels[o + 0] = 60;
                        pixels[o + 1] = 120;
                        pixels[o + 2] = 220;
                        pixels[o + 3] = 255;
                    }
                    else
                    {
                        pixels[o + 0] = 245;
                        pixels[o + 1] = 245;
                        pixels[o + 2] = 245;
                        pixels[o + 3] = 90;
                    }
                }
            }

            return PixelBuffer.Create(pixels, new PixelSize(width, height), stride,
                PixelFormats.Rgba8888, AlphaFormat.Unpremul, new Vector(96, 96));
        }
    }
}
