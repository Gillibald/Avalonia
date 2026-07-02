using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit
{
    /// <summary>
    /// Reads a locked framebuffer into straight RGBA bytes for pixel-level assertions.
    /// Covers the formats the generated fixtures decode to; unknown formats fail with a
    /// pointer to extend this reader.
    /// </summary>
    public static class FramebufferPixels
    {
        /// <summary>
        /// Returns the framebuffer's pixels as row-major RGBA, four bytes per pixel.
        /// Alpha-less formats read as fully opaque; 16-bit channels expand by bit
        /// replication.
        /// </summary>
        public static byte[] ReadRgba(ILockedFramebuffer framebuffer)
        {
            _ = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));

            var size = framebuffer.Size;
            var format = framebuffer.Format;
            var bytesPerPixel = (format.BitsPerPixel + 7) / 8;

            // The last row of an interop framebuffer may lack its stride padding, so only
            // the pixel bytes of each row are read.
            var minRowBytes = (size.Width * format.BitsPerPixel + 7) / 8;
            var result = new byte[size.Width * size.Height * 4];
            var row = new byte[minRowBytes];

            for (var y = 0; y < size.Height; y++)
            {
                Marshal.Copy(framebuffer.Address + y * framebuffer.RowBytes, row, 0, minRowBytes);

                var target = y * size.Width * 4;

                for (var x = 0; x < size.Width; x++)
                {
                    var source = x * bytesPerPixel;
                    byte r, g, b, a;

                    if (format == PixelFormats.Rgba8888)
                    {
                        (r, g, b, a) = (row[source], row[source + 1], row[source + 2], row[source + 3]);
                    }
                    else if (format == PixelFormats.Bgra8888)
                    {
                        (b, g, r, a) = (row[source], row[source + 1], row[source + 2], row[source + 3]);
                    }
                    else if (format == PixelFormats.Rgb24)
                    {
                        (r, g, b, a) = (row[source], row[source + 1], row[source + 2], (byte)255);
                    }
                    else if (format == PixelFormats.Bgr24)
                    {
                        (b, g, r, a) = (row[source], row[source + 1], row[source + 2], (byte)255);
                    }
                    else if (format == PixelFormats.Rgb32)
                    {
                        (r, g, b, a) = (row[source], row[source + 1], row[source + 2], (byte)255);
                    }
                    else if (format == PixelFormats.Bgr32)
                    {
                        (b, g, r, a) = (row[source], row[source + 1], row[source + 2], (byte)255);
                    }
                    else if (format == PixelFormats.Rgb565 || format == PixelFormats.Bgr565)
                    {
                        var value = (ushort)(row[source] | (row[source + 1] << 8));

                        r = Expand5((value >> 11) & 0x1F);
                        g = Expand6((value >> 5) & 0x3F);
                        b = Expand5(value & 0x1F);
                        a = 255;
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"The test kit cannot read pixels in format '{format}'; extend {nameof(FramebufferPixels)}.");
                    }

                    var offset = target + x * 4;

                    result[offset] = r;
                    result[offset + 1] = g;
                    result[offset + 2] = b;
                    result[offset + 3] = a;
                }
            }

            return result;
        }

        private static byte Expand5(int value) => (byte)((value * 255 + 15) / 31);

        private static byte Expand6(int value) => (byte)((value * 255 + 31) / 63);
    }
}
