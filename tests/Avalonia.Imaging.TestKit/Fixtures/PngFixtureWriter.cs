using System;
using System.IO;
using System.IO.Compression;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A minimal, dependency-free PNG writer for deterministic in-memory fixtures:
    /// 8-bit RGB or RGBA, no interlacing, filter type None on every scanline, zlib
    /// framing around a <see cref="DeflateStream"/>. Scanlines are compressed one at a
    /// time, so memory use is bounded by the compressed output, never the raw pixels.
    /// </summary>
    public static class PngFixtureWriter
    {
        private static readonly byte[] s_signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static uint[]? s_crcTable;

        /// <summary>Writes an 8-bit RGB (color type 2) PNG.</summary>
        public static byte[] WriteRgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixelAt)
        {
            _ = pixelAt ?? throw new ArgumentNullException(nameof(pixelAt));

            return Write(width, height, colorType: 2, bytesPerPixel: 3, (row, y) =>
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = pixelAt(x, y);
                    var offset = 1 + x * 3;

                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                }
            });
        }

        /// <summary>Writes an 8-bit RGBA (color type 6) PNG.</summary>
        public static byte[] WriteRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixelAt)
        {
            _ = pixelAt ?? throw new ArgumentNullException(nameof(pixelAt));

            return Write(width, height, colorType: 6, bytesPerPixel: 4, (row, y) =>
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b, a) = pixelAt(x, y);
                    var offset = 1 + x * 4;

                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                    row[offset + 3] = a;
                }
            });
        }

        /// <summary>
        /// Writes a single-color RGB PNG. The scanline is filled once and re-compressed
        /// per row, so huge images never materialize their raw pixel data.
        /// </summary>
        public static byte[] WriteFlatRgb(int width, int height, byte r, byte g, byte b)
        {
            var filled = false;

            return Write(width, height, colorType: 2, bytesPerPixel: 3, (row, _) =>
            {
                if (filled)
                    return;

                for (var x = 0; x < width; x++)
                {
                    var offset = 1 + x * 3;

                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                }

                filled = true;
            });
        }

        /// <summary>
        /// Returns a prefix of a PNG, cut in the middle of its first IDAT chunk's data.
        /// </summary>
        public static byte[] TruncateMidIdat(byte[] png)
        {
            _ = png ?? throw new ArgumentNullException(nameof(png));

            var (dataStart, dataLength) = FindFirstIdat(png);
            var cut = dataStart + Math.Max(1, dataLength / 2);
            var result = new byte[cut];

            Array.Copy(png, result, cut);

            return result;
        }

        /// <summary>
        /// Returns a copy of a PNG whose first IDAT chunk stores a wrong CRC; the zlib
        /// data itself is untouched.
        /// </summary>
        public static byte[] CorruptIdatCrc(byte[] png)
        {
            _ = png ?? throw new ArgumentNullException(nameof(png));

            var (dataStart, dataLength) = FindFirstIdat(png);
            var result = (byte[])png.Clone();

            result[dataStart + dataLength + 3] ^= 0xFF;

            return result;
        }

        private static byte[] Write(int width, int height, byte colorType, int bytesPerPixel, Action<byte[], int> fillRow)
        {
            if (width < 1)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 1)
                throw new ArgumentOutOfRangeException(nameof(height));

            using var output = new MemoryStream();

            output.Write(s_signature, 0, s_signature.Length);

            var ihdr = new byte[13];

            WriteUInt32BigEndian(ihdr, 0, (uint)width);
            WriteUInt32BigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;         // bit depth
            ihdr[9] = colorType; // 2 = RGB, 6 = RGBA; compression, filter and interlace stay 0
            WriteChunk(output, "IHDR", ihdr);

            using (var idat = new MemoryStream())
            {
                idat.WriteByte(0x78); // zlib CMF: deflate, 32K window
                idat.WriteByte(0x9C); // zlib FLG: default compression, no preset dictionary

                var adler = 1u;
                var row = new byte[1 + width * bytesPerPixel];

                row[0] = 0; // filter type None; fillRow only writes pixel bytes

                using (var deflate = new DeflateStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                {
                    for (var y = 0; y < height; y++)
                    {
                        fillRow(row, y);
                        deflate.Write(row, 0, row.Length);
                        adler = UpdateAdler32(adler, row);
                    }
                }

                var trailer = new byte[4];

                WriteUInt32BigEndian(trailer, 0, adler);
                idat.Write(trailer, 0, 4);
                WriteChunk(output, "IDAT", idat.ToArray());
            }

            WriteChunk(output, "IEND", Array.Empty<byte>());

            return output.ToArray();
        }

        private static (int DataStart, int DataLength) FindFirstIdat(byte[] png)
        {
            // Chunks follow the 8-byte signature as length, type, data, CRC.
            var offset = s_signature.Length;

            while (offset + 8 <= png.Length)
            {
                var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];

                if (png[offset + 4] == (byte)'I' && png[offset + 5] == (byte)'D' &&
                    png[offset + 6] == (byte)'A' && png[offset + 7] == (byte)'T')
                {
                    return (offset + 8, length);
                }

                offset += 8 + length + 4;
            }

            throw new InvalidOperationException("The PNG has no IDAT chunk.");
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            var header = new byte[8];

            WriteUInt32BigEndian(header, 0, (uint)data.Length);

            for (var i = 0; i < 4; i++)
                header[4 + i] = (byte)type[i];

            output.Write(header, 0, 8);
            output.Write(data, 0, data.Length);

            var crc = 0xFFFFFFFFu;

            crc = UpdateCrc32(crc, header.AsSpan(4, 4));
            crc = UpdateCrc32(crc, data);

            var trailer = new byte[4];

            WriteUInt32BigEndian(trailer, 0, crc ^ 0xFFFFFFFFu);
            output.Write(trailer, 0, 4);
        }

        private static void WriteUInt32BigEndian(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
        {
            var table = s_crcTable ??= CreateCrcTable();

            foreach (var value in data)
                crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];

            for (var i = 0u; i < 256; i++)
            {
                var entry = i;

                for (var bit = 0; bit < 8; bit++)
                    entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;

                table[i] = entry;
            }

            return table;
        }

        private static uint UpdateAdler32(uint adler, ReadOnlySpan<byte> data)
        {
            const uint modulus = 65521;

            var a = adler & 0xFFFF;
            var b = adler >> 16;
            var offset = 0;

            while (offset < data.Length)
            {
                // 5552 is the classic zlib NMAX: the longest run before the 32-bit sums can overflow.
                var run = Math.Min(5552, data.Length - offset);

                for (var i = 0; i < run; i++)
                {
                    a += data[offset + i];
                    b += a;
                }

                a %= modulus;
                b %= modulus;
                offset += run;
            }

            return (b << 16) | a;
        }
    }
}
