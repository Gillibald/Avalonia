using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Avalonia.PixelMan;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// Reads image dimensions, resolution and frame count from container headers. PixelMan
    /// identifies the codec and the stored layout without decoding, but its decoders
    /// produce the size only by decoding the frame, which identify must not do.
    /// </summary>
    /// <remarks>
    /// Resolutions follow PixelMan's own conversion (pixels per metre times 0.0254, unrounded),
    /// so identify and decode report the same DPI. A file without a resolution reports none.
    /// </remarks>
    internal static class PixelManHeaderReader
    {
        private const int MaxTiffPages = 4096;

        public readonly record struct Header(PixelSize Size, Vector? Dpi, int? FrameCount);

        /// <summary>Reads bytes at an absolute offset, returning how many it read.</summary>
        public delegate int ReadAt(long offset, Span<byte> buffer);

        /// <summary>
        /// Reads the header of a container, returning false when the header is malformed or
        /// lies beyond the readable data.
        /// </summary>
        public static bool TryRead(PmCodecKind kind, ReadAt readAt, out Header header)
        {
            header = default;

            try
            {
                if (kind == PmCodecKind.Png)
                    return TryReadPng(readAt, out header);
                if (kind == PmCodecKind.Gif)
                    return TryReadGif(readAt, out header);
                if (kind == PmCodecKind.Bmp)
                    return TryReadBmp(readAt, out header);
                if (kind == PmCodecKind.Jpeg)
                    return TryReadJpeg(readAt, out header);
                if (kind == PmCodecKind.Tiff)
                    return TryReadTiff(readAt, out header);
            }
            catch (EndOfStreamException)
            {
            }

            return false;
        }

        public static ReadAt FromSpan(byte[] data) => (offset, buffer) =>
        {
            if (offset < 0 || offset >= data.Length)
                return 0;

            var count = (int)Math.Min(buffer.Length, data.Length - offset);

            data.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        };

        public static ReadAt FromStream(Stream stream, long origin) => (offset, buffer) =>
        {
            stream.Position = origin + offset;

            var total = 0;

            while (total < buffer.Length)
            {
                var read = stream.Read(buffer.Slice(total));

                if (read == 0)
                    break;

                total += read;
            }

            return total;
        };

        private static void ReadExactly(ReadAt readAt, long offset, Span<byte> buffer)
        {
            if (readAt(offset, buffer) != buffer.Length)
                throw new EndOfStreamException();
        }

        private static bool TryReadPng(ReadAt readAt, out Header header)
        {
            header = default;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> ihdr = stackalloc byte[8];
            Span<byte> phys = stackalloc byte[9];

            ReadExactly(readAt, 8, chunk);

            if (!chunk.Slice(4, 4).SequenceEqual("IHDR"u8))
                return false;

            ReadExactly(readAt, 16, ihdr);

            var width = BinaryPrimitives.ReadInt32BigEndian(ihdr);
            var height = BinaryPrimitives.ReadInt32BigEndian(ihdr.Slice(4));
            Vector? dpi = null;

            // pHYs precedes the image data.
            long offset = 8;

            for (var i = 0; i < 1024; i++)
            {
                if (readAt(offset, chunk) != 8)
                    break;

                var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                var type = chunk.Slice(4, 4);

                if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                    break;

                if (type.SequenceEqual("pHYs"u8) && length == 9)
                {
                    ReadExactly(readAt, offset + 8, phys);

                    var x = BinaryPrimitives.ReadUInt32BigEndian(phys);
                    var y = BinaryPrimitives.ReadUInt32BigEndian(phys.Slice(4));

                    if (phys[8] == 1)
                        dpi = new Vector(x * 0.0254, y * 0.0254);
                }

                offset += 12 + (long)length;
            }

            header = new Header(new PixelSize(width, height), dpi, 1);
            return width > 0 && height > 0;
        }

        private static bool TryReadGif(ReadAt readAt, out Header header)
        {
            Span<byte> screen = stackalloc byte[4];

            ReadExactly(readAt, 6, screen);

            var width = BinaryPrimitives.ReadUInt16LittleEndian(screen);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(screen.Slice(2));

            // Counting frames needs a walk over every block, as a forward-only decoder does.
            header = new Header(new PixelSize(width, height), null, null);
            return width > 0 && height > 0;
        }

        private static bool TryReadBmp(ReadAt readAt, out Header header)
        {
            header = default;

            Span<byte> info = stackalloc byte[40];
            var read = readAt(14, info);

            if (read < 12)
                return false;

            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info);

            if (headerSize == 12)
            {
                header = new Header(new PixelSize(BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(6))), null, 1);
                return header.Size.Width > 0 && header.Size.Height > 0;
            }

            if (read < 40)
                return false;

            var width = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(4));
            var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(info.Slice(8)));
            var x = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(24));
            var y = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(28));

            header = new Header(new PixelSize(width, height), x > 0 && y > 0 ? new Vector(x * 0.0254, y * 0.0254) : null, 1);
            return width > 0 && height > 0;
        }

        private static bool TryReadJpeg(ReadAt readAt, out Header header)
        {
            header = default;

            Span<byte> marker = stackalloc byte[4];
            Span<byte> frame = stackalloc byte[5];
            Span<byte> jfif = stackalloc byte[12];
            Vector? dpi = null;
            long offset = 2;

            for (var i = 0; i < 4096; i++)
            {
                ReadExactly(readAt, offset, marker);

                if (marker[0] != 0xFF)
                    return false;

                var code = marker[1];

                // Fill bytes before a marker.
                if (code == 0xFF)
                {
                    offset++;
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(marker.Slice(2));

                if (code is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
                {
                    offset += 2;
                    continue;
                }

                if (code is 0xD9 or 0xDA)
                    return false;

                // SOF0-SOF15, apart from DHT (C4), JPG (C8) and DAC (CC).
                if (code is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    ReadExactly(readAt, offset + 4, frame);

                    var height = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(1));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3));

                    header = new Header(new PixelSize(width, height), dpi, 1);
                    return width > 0 && height > 0;
                }

                if (code == 0xE0 && length >= 16)
                {
                    ReadExactly(readAt, offset + 4, jfif);

                    if (jfif.Slice(0, 5).SequenceEqual("JFIF\0"u8))
                    {
                        var x = BinaryPrimitives.ReadUInt16BigEndian(jfif.Slice(8));
                        var y = BinaryPrimitives.ReadUInt16BigEndian(jfif.Slice(10));

                        dpi = jfif[7] switch
                        {
                            1 when x > 0 && y > 0 => new Vector(x, y),
                            2 when x > 0 && y > 0 => new Vector(x * 2.54, y * 2.54),
                            _ => dpi,
                        };
                    }
                }

                offset += 2 + length;
            }

            return false;
        }

        private static bool TryReadTiff(ReadAt readAt, out Header header)
        {
            header = default;

            Span<byte> start = stackalloc byte[8];

            ReadExactly(readAt, 0, start);

            var little = start[0] == (byte)'I';

            // BigTIFF (version 43) is left to the decoder.
            if (Read16(start.Slice(2), little) != 42)
                return false;

            var offset = (long)Read32(start.Slice(4), little);
            var visited = new HashSet<long>();
            var pages = 0;
            PixelSize size = default;
            Vector? dpi = null;
            Span<byte> count = stackalloc byte[2];
            Span<byte> entry = stackalloc byte[12];
            Span<byte> next = stackalloc byte[4];

            while (offset != 0 && pages < MaxTiffPages && visited.Add(offset))
            {
                ReadExactly(readAt, offset, count);

                var entries = Read16(count, little);

                if (pages == 0)
                {
                    uint width = 0, height = 0, unit = 2;
                    double? x = null, y = null;

                    for (var i = 0; i < entries; i++)
                    {
                        ReadExactly(readAt, offset + 2 + i * 12, entry);

                        var tag = Read16(entry, little);
                        var type = Read16(entry.Slice(2), little);
                        var value = type == 3 ? Read16(entry.Slice(8), little) : Read32(entry.Slice(8), little);

                        switch (tag)
                        {
                            case 256: width = value; break;
                            case 257: height = value; break;
                            case 296: unit = value; break;
                            case 282 when type == 5: x = ReadRational(readAt, Read32(entry.Slice(8), little), little); break;
                            case 283 when type == 5: y = ReadRational(readAt, Read32(entry.Slice(8), little), little); break;
                        }
                    }

                    size = new PixelSize((int)width, (int)height);

                    // A missing unit means inches; unit 1 has no absolute size.
                    if (x > 0 && y > 0 && unit is 2 or 3)
                        dpi = unit == 3 ? new Vector(x.Value * 2.54, y.Value * 2.54) : new Vector(x.Value, y.Value);
                }

                ReadExactly(readAt, offset + 2 + entries * 12L, next);

                offset = Read32(next, little);
                pages++;
            }

            header = new Header(size, dpi, pages);
            return size.Width > 0 && size.Height > 0;
        }

        private static double? ReadRational(ReadAt readAt, long offset, bool little)
        {
            Span<byte> rational = stackalloc byte[8];

            ReadExactly(readAt, offset, rational);

            var denominator = Read32(rational.Slice(4), little);

            return denominator == 0 ? null : (double)Read32(rational, little) / denominator;
        }

        private static uint Read16(ReadOnlySpan<byte> data, bool little)
            => little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

        private static uint Read32(ReadOnlySpan<byte> data, bool little)
            => little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
    }
}
