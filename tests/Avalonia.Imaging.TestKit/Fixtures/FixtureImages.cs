using System;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// The generated image fixtures the contract tests run against. Every fixture is
    /// deterministic and generated in memory on first use.
    /// </summary>
    public static class FixtureImages
    {
        /// <summary>The catalog format name of the PNG fixtures.</summary>
        public const string PngFormatName = "PNG";

        /// <summary>The catalog format name of the BMP fixtures.</summary>
        public const string BmpFormatName = "BMP";

        private const int HugeDimension = 16384;

        /// <summary>A 4x4 8-bit RGB PNG with a distinct color per pixel.</summary>
        public static ImageFixture Rgb4x4Png { get; } = new(
            nameof(Rgb4x4Png), PngFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            BuildRgbaReference(4, 4, (x, y) => WithOpaqueAlpha(Rgb4x4Pixel(x, y))),
            () => PngFixtureWriter.WriteRgb(4, 4, Rgb4x4Pixel));

        /// <summary>A 4x4 8-bit RGBA PNG whose alpha runs from 0 to 255 across the pixels.</summary>
        public static ImageFixture AlphaGradientPng { get; } = new(
            nameof(AlphaGradientPng), PngFormatName, new PixelSize(4, 4), expectedHasAlpha: true,
            BuildRgbaReference(4, 4, AlphaGradientPixel),
            () => PngFixtureWriter.WriteRgba(4, 4, AlphaGradientPixel));

        /// <summary>
        /// A 16384x16384 flat-color RGB PNG: small when encoded, decompression-bomb
        /// sized when decoded.
        /// </summary>
        public static ImageFixture Huge16kPng { get; } = new(
            nameof(Huge16kPng), PngFormatName, new PixelSize(HugeDimension, HugeDimension), expectedHasAlpha: false,
            expectedRgba: null,
            () => PngFixtureWriter.WriteFlatRgb(HugeDimension, HugeDimension, 200, 100, 50));

        /// <summary>The pixel pattern of <see cref="Rgb4x4Png"/> as a 24-bit BI_RGB BMP.</summary>
        public static ImageFixture Rgb4x4Bmp { get; } = new(
            nameof(Rgb4x4Bmp), BmpFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            BuildRgbaReference(4, 4, (x, y) => WithOpaqueAlpha(Rgb4x4Pixel(x, y))),
            () => BmpFixtureWriter.WriteRgb24(4, 4, Rgb4x4Pixel));

        /// <summary>
        /// A 4x4 16-bit 565 BI_BITFIELDS BMP of pure colors, chosen so 565 packing and
        /// any sane unpacking reproduce the reference exactly.
        /// </summary>
        public static ImageFixture Rgb565Bmp { get; } = new(
            nameof(Rgb565Bmp), BmpFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            BuildRgbaReference(4, 4, (x, y) => WithOpaqueAlpha(Rgb565Pixel(x, y))),
            () => BmpFixtureWriter.WriteRgb565(4, 4, Rgb565Pixel));

        /// <summary>A valid PNG prefix cut in the middle of the IDAT data.</summary>
        public static ImageFixture TruncatedPng { get; } = new(
            nameof(TruncatedPng), PngFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            expectedRgba: null,
            () => PngFixtureWriter.TruncateMidIdat(Rgb4x4Png.EncodedBytes.ToArray()));

        /// <summary>
        /// The bytes of <see cref="Rgb4x4Png"/> with a wrong CRC stored on the IDAT
        /// chunk; the zlib data is intact.
        /// </summary>
        public static ImageFixture CorruptCrcPng { get; } = new(
            nameof(CorruptCrcPng), PngFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            expectedRgba: null,
            () => PngFixtureWriter.CorruptIdatCrc(Rgb4x4Png.EncodedBytes.ToArray()));

        /// <summary>Deterministic pseudo-random bytes carrying no known image magic.</summary>
        public static ImageFixture NotAnImage { get; } = new(
            nameof(NotAnImage), string.Empty, default, expectedHasAlpha: false,
            expectedRgba: null,
            BuildNotAnImage);

        /// <summary>The tEXt keyword stored in <see cref="PngWithTextMetadata"/>.</summary>
        public const string PngTextKeyword = "Title";

        /// <summary>The tEXt value stored in <see cref="PngWithTextMetadata"/>.</summary>
        public const string PngTextValue = "Hello tEXt";

        /// <summary>The pattern of <see cref="Rgb4x4Png"/> plus one tEXt metadata entry.</summary>
        public static ImageFixture PngWithTextMetadata { get; } = new(
            nameof(PngWithTextMetadata), PngFormatName, new PixelSize(4, 4), expectedHasAlpha: false,
            BuildRgbaReference(4, 4, (x, y) => WithOpaqueAlpha(Rgb4x4Pixel(x, y))),
            () => PngFixtureWriter.WriteRgbWithText(4, 4, Rgb4x4Pixel, PngTextKeyword, PngTextValue));

        /// <summary>A 16x16 8-bit RGB PNG gradient with a distinct color per pixel.</summary>
        public static ImageFixture Gradient16Png { get; } = new(
            nameof(Gradient16Png), PngFormatName, new PixelSize(16, 16), expectedHasAlpha: false,
            BuildRgbaReference(16, 16, (x, y) => WithOpaqueAlpha(Gradient16Pixel(x, y))),
            () => PngFixtureWriter.WriteRgb(16, 16, Gradient16Pixel));

        /// <summary>
        /// A 128x128 RGB PNG for tests where frame-sized buffers must dominate scratch
        /// memory; no pixel reference, its content is never compared.
        /// </summary>
        public static ImageFixture Gradient128Png { get; } = new(
            nameof(Gradient128Png), PngFormatName, new PixelSize(128, 128), expectedHasAlpha: false,
            expectedRgba: null,
            () => PngFixtureWriter.WriteRgb(128, 128, Gradient128Pixel));

        private static (byte R, byte G, byte B) Rgb4x4Pixel(int x, int y) =>
            ((byte)(x * 85), (byte)(y * 85), (byte)((x ^ y) * 85));

        private static (byte R, byte G, byte B) Gradient16Pixel(int x, int y) =>
            ((byte)(x * 17), (byte)(y * 17), (byte)((x + y) * 8));

        private static (byte R, byte G, byte B) Gradient128Pixel(int x, int y) =>
            ((byte)(x * 2), (byte)(y * 2), (byte)((x ^ y) * 2));

        private static (byte R, byte G, byte B, byte A) AlphaGradientPixel(int x, int y) =>
            (GradientLevel(x), GradientLevel(y), GradientLevel(3 - x), (byte)((y * 4 + x) * 17));

        // Channel levels whose products with the 17-step alphas divide by 255 without a
        // remainder, so the premultiplied reference is integral no matter how a backend
        // rounds its premultiplication.
        private static byte GradientLevel(int index) => index switch
        {
            0 => 0,
            1 => 120,
            2 => 240,
            _ => 255,
        };

        // Pure 0/255 channels survive 565 packing and any sane unpacking exactly.
        private static (byte R, byte G, byte B) Rgb565Pixel(int x, int y) => ((y * 4 + x) % 8) switch
        {
            0 => (0, 0, 0),
            1 => (255, 255, 255),
            2 => (255, 0, 0),
            3 => (0, 255, 0),
            4 => (0, 0, 255),
            5 => (255, 255, 0),
            6 => (0, 255, 255),
            _ => (255, 0, 255),
        };

        private static (byte R, byte G, byte B, byte A) WithOpaqueAlpha((byte R, byte G, byte B) color) =>
            (color.R, color.G, color.B, 255);

        private static byte[] BuildRgbaReference(int width, int height,
            Func<int, int, (byte R, byte G, byte B, byte A)> pixelAt)
        {
            var result = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b, a) = pixelAt(x, y);
                    var offset = (y * width + x) * 4;

                    result[offset] = r;
                    result[offset + 1] = g;
                    result[offset + 2] = b;
                    result[offset + 3] = a;
                }
            }

            return result;
        }

        private static byte[] BuildNotAnImage()
        {
            var bytes = new byte[256];
            var state = 0x12345678u;

            for (var i = 0; i < bytes.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                bytes[i] = (byte)(state >> 24);
            }

            // No known container magic starts with a run of 0xAA bytes.
            for (var i = 0; i < 12; i++)
                bytes[i] = 0xAA;

            return bytes;
        }
    }
}
