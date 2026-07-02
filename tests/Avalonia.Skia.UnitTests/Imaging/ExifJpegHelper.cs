using System;
using SkiaSharp;

namespace Avalonia.Skia.UnitTests.Imaging
{
    /// <summary>
    /// Builds JPEG test payloads carrying an EXIF orientation. SkiaSharp cannot write
    /// EXIF, so a minimal APP1 segment (one IFD entry: orientation) is inserted after
    /// the SOI marker.
    /// </summary>
    internal static class ExifJpegHelper
    {
        public static byte[] InjectExifOrientation(byte[] jpeg, byte orientation)
        {
            var app1 = new byte[]
            {
                0xFF, 0xE1, 0x00, 0x22,                                 // APP1, length 34
                (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00, // Exif\0\0
                0x49, 0x49, 0x2A, 0x00,                                 // TIFF header, little endian
                0x08, 0x00, 0x00, 0x00,                                 // IFD0 offset
                0x01, 0x00,                                             // one directory entry
                0x12, 0x01, 0x03, 0x00,                                 // tag 0x0112, type SHORT
                0x01, 0x00, 0x00, 0x00,                                 // count 1
                orientation, 0x00, 0x00, 0x00,                          // value
                0x00, 0x00, 0x00, 0x00,                                 // next IFD offset
            };

            var result = new byte[jpeg.Length + app1.Length];

            result[0] = jpeg[0];
            result[1] = jpeg[1];
            app1.CopyTo(result, 2);
            Array.Copy(jpeg, 2, result, 2 + app1.Length, jpeg.Length - 2);

            return result;
        }

        /// <summary>
        /// A 16x8 JPEG, left half red and right half green, tagged with EXIF
        /// orientation 6 (rotate 90 clockwise to display). The halves are aligned to
        /// 8x8 DCT blocks and encoded at quality 100 without chroma subsampling, so
        /// every block is flat and survives the round trip nearly exactly.
        /// </summary>
        public static byte[] CreateHalvedOrientation6Jpeg(out PixelSize rawSize, out byte[] orientedRgba)
        {
            rawSize = new PixelSize(16, 8);

            using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Bgra8888, SKAlphaType.Opaque));

            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    bitmap.SetPixel(x, y, x < 8 ? new SKColor(255, 0, 0) : new SKColor(0, 255, 0));
                }
            }

            using var pixmap = bitmap.PeekPixels();
            using var data = pixmap.Encode(new SKJpegEncoderOptions(100, SKJpegEncoderDownsample.Downsample444,
                SKJpegEncoderAlphaOption.Ignore)) ?? throw new InvalidOperationException("JPEG encoding failed.");

            // Rotated 90 degrees clockwise, the raw left half (red) lands on top: the
            // oriented 8x16 image is red for rows 0..7 and green for rows 8..15.
            orientedRgba = new byte[8 * 16 * 4];

            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var offset = (y * 8 + x) * 4;

                    orientedRgba[offset] = y < 8 ? (byte)255 : (byte)0;       // R
                    orientedRgba[offset + 1] = y < 8 ? (byte)0 : (byte)255;   // G
                    orientedRgba[offset + 2] = 0;                             // B
                    orientedRgba[offset + 3] = 255;                           // A
                }
            }

            return InjectExifOrientation(data.ToArray(), orientation: 6);
        }
    }
}
