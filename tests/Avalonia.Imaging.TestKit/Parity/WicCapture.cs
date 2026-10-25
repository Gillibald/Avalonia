using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Avalonia.Imaging.TestKit.Parity
{
    /// <summary>
    /// A decode captured from WPF's <c>BitmapDecoder</c> in the PixelMan corpus (<c>RAWIMG</c>
    /// files): the frame's WPF pixel format name, size, resolution, palette and rows.
    /// </summary>
    public sealed class WicCapture
    {
        private WicCapture(string formatName, int width, int height, int stride, double dpiX, double dpiY,
            int bitsPerPixel, uint[] palette, byte[] pixels)
        {
            FormatName = formatName;
            Width = width;
            Height = height;
            Stride = stride;
            DpiX = dpiX;
            DpiY = dpiY;
            BitsPerPixel = bitsPerPixel;
            Palette = palette;
            Pixels = pixels;
        }

        /// <summary>Gets the WPF <c>PixelFormats</c> member name, such as <c>Pbgra32</c>.</summary>
        public string FormatName { get; }

        public int Width { get; }

        public int Height { get; }

        public int Stride { get; }

        public double DpiX { get; }

        public double DpiY { get; }

        public int BitsPerPixel { get; }

        /// <summary>Gets the palette as 0xAARRGGBB values; empty for formats without one.</summary>
        public uint[] Palette { get; }

        public byte[] Pixels { get; }

        /// <summary>
        /// Reads a capture. Version 1 stores the rows as they are, version 2 as raw deflate
        /// after their uncompressed length.
        /// </summary>
        public static WicCapture Read(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));

            if (Encoding.ASCII.GetString(reader.ReadBytes(6)) != "RAWIMG")
                throw new InvalidDataException($"{path} is not a RAWIMG capture.");

            var version = reader.ReadUInt16();

            if (version is not (1 or 2))
                throw new InvalidDataException($"{path} has unsupported RAWIMG version {version}.");

            var width = checked((int)reader.ReadUInt32());
            var height = checked((int)reader.ReadUInt32());
            var stride = checked((int)reader.ReadUInt32());
            var dpiX = reader.ReadDouble();
            var dpiY = reader.ReadDouble();
            var bitsPerPixel = reader.ReadUInt16();
            var formatName = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            var palette = new uint[reader.ReadUInt32()];

            for (var i = 0; i < palette.Length; i++)
            {
                // Entries are stored B, G, R, A.
                var entry = reader.ReadBytes(4);
                palette[i] = (uint)(entry[3] << 24 | entry[2] << 16 | entry[1] << 8 | entry[0]);
            }

            byte[] pixels;

            if (version == 2)
            {
                pixels = new byte[checked((int)reader.ReadUInt32())];

                using var inflate = new DeflateStream(reader.BaseStream, CompressionMode.Decompress, leaveOpen: true);

                inflate.ReadExactly(pixels);
            }
            else
            {
                pixels = reader.ReadBytes(checked(stride * height));
            }

            if (pixels.Length < (long)stride * height)
                throw new InvalidDataException($"{path} holds fewer rows than its header declares.");

            return new WicCapture(formatName, width, height, stride, dpiX, dpiY, bitsPerPixel, palette, pixels);
        }
    }
}
