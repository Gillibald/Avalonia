using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A minimal, dependency-free BMP writer for deterministic in-memory fixtures:
    /// bottom-up rows, 24-bit BI_RGB or 16-bit 565 BI_BITFIELDS.
    /// </summary>
    public static class BmpFixtureWriter
    {
        /// <summary>Writes a 24-bit BI_RGB BMP.</summary>
        public static byte[] WriteRgb24(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixelAt)
        {
            Validate(width, height, pixelAt);

            var rowBytes = (width * 3 + 3) & ~3;

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            WriteHeaders(writer, width, height, bitCount: 24, compression: 0, rowBytes, writeMasks: false);

            for (var y = height - 1; y >= 0; y--)
            {
                var written = 0;

                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = pixelAt(x, y);

                    writer.Write(b);
                    writer.Write(g);
                    writer.Write(r);
                    written += 3;
                }

                for (; written < rowBytes; written++)
                    writer.Write((byte)0);
            }

            writer.Flush();

            return output.ToArray();
        }

        /// <summary>Writes a 16-bit 565 BI_BITFIELDS BMP.</summary>
        public static byte[] WriteRgb565(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixelAt)
        {
            Validate(width, height, pixelAt);

            var rowBytes = (width * 2 + 3) & ~3;

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            WriteHeaders(writer, width, height, bitCount: 16, compression: 3, rowBytes, writeMasks: true);

            for (var y = height - 1; y >= 0; y--)
            {
                var written = 0;

                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = pixelAt(x, y);
                    var packed = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

                    writer.Write(packed);
                    written += 2;
                }

                for (; written < rowBytes; written++)
                    writer.Write((byte)0);
            }

            writer.Flush();

            return output.ToArray();
        }

        private static void Validate(int width, int height, Delegate pixelAt)
        {
            if (width < 1)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 1)
                throw new ArgumentOutOfRangeException(nameof(height));
            _ = pixelAt ?? throw new ArgumentNullException(nameof(pixelAt));
        }

        private static void WriteHeaders(BinaryWriter writer, int width, int height, ushort bitCount,
            uint compression, int rowBytes, bool writeMasks)
        {
            var maskBytes = writeMasks ? 12 : 0;
            var offBits = 14 + 40 + maskBytes;
            var imageSize = rowBytes * height;

            // BITMAPFILEHEADER
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write((uint)(offBits + imageSize));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((uint)offBits);

            // BITMAPINFOHEADER
            writer.Write(40u);
            writer.Write(width);
            writer.Write(height); // positive height: bottom-up
            writer.Write((ushort)1);
            writer.Write(bitCount);
            writer.Write(compression);
            writer.Write((uint)imageSize);
            writer.Write(2835); // 72 DPI in pixels per meter
            writer.Write(2835);
            writer.Write(0u);
            writer.Write(0u);

            if (writeMasks)
            {
                writer.Write(0xF800u); // red
                writer.Write(0x07E0u); // green
                writer.Write(0x001Fu); // blue
            }
        }
    }
}
