using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    // Expected bytes are WPF's FormatConvertedBitmap of the same pixels, which dithers through WIC.
    public class ErrorDiffusionTests
    {
        private static readonly PixelConversionOptions s_dither = new() { Dither = BitmapDitherMode.ErrorDiffusion };

        private static readonly byte[] s_gray8x3 =
        {
            122, 129, 51, 215, 84, 6, 227, 175,
            212, 84, 211, 35, 122, 151, 217, 209,
            153, 26, 145, 55, 12, 71, 170, 220,
        };

        private static readonly byte[] s_bgr8x4 =
        {
            148, 42, 8, 157, 216, 194, 183, 66, 98, 136, 227, 7, 113, 232, 12, 54, 43, 255, 215, 10, 220, 183, 29, 105,
            18, 188, 88, 110, 185, 188, 144, 160, 43, 242, 90, 247, 229, 83, 93, 91, 165, 100, 58, 42, 50, 218, 19, 141,
            143, 169, 55, 189, 147, 156, 232, 111, 132, 76, 54, 61, 252, 121, 168, 72, 51, 196, 85, 230, 17, 28, 213, 201,
            11, 83, 115, 6, 113, 29, 227, 47, 177, 8, 193, 251, 98, 18, 38, 135, 238, 100, 235, 151, 110, 46, 52, 86,
        };

        private static readonly BitmapPalette s_blackRedGreenBlue = new(new[]
        {
            Colors.Black, Color.FromRgb(255, 0, 0), Color.FromRgb(0, 128, 0), Color.FromRgb(0, 0, 255),
        });

        private static byte[] Convert(byte[] pixels, int width, int height, PixelFormat source, PixelFormat target,
            PixelConversionOptions options)
        {
            var stride = pixels.Length / height;
            var buffer = PixelBuffer.Create(pixels, new PixelSize(width, height), stride, source, AlphaFormat.Unpremul);

            return buffer.Convert(target, AlphaFormat.Unpremul, options).Pixels.ToArray();
        }

        private static PixelConversionOptions WithPalette(BitmapPalette palette, double alphaThreshold = 0) => new()
        {
            Dither = BitmapDitherMode.ErrorDiffusion,
            Palette = palette,
            AlphaThresholdPercent = alphaThreshold,
        };

        [Fact]
        public void Flat_Gray_Should_Start_The_Serpentine_Scan_From_The_Right()
        {
            var pixels = new byte[16];
            System.Array.Fill(pixels, (byte)128);

            var result = Convert(pixels, 8, 2, PixelFormats.Gray8, PixelFormats.BlackWhite, s_dither);

            Assert.Equal(new byte[] { 0b0101_0101, 0b1010_1010 }, result);
        }

        [Fact]
        public void Gray_Targets_Should_Match_WIC()
        {
            Assert.Equal(new byte[] { 83, 171, 163 },
                Convert(s_gray8x3, 8, 3, PixelFormats.Gray8, PixelFormats.BlackWhite, s_dither));
            Assert.Equal(new byte[] { 99, 78, 216, 171, 137, 27 },
                Convert(s_gray8x3, 8, 3, PixelFormats.Gray8, PixelFormats.Gray2, s_dither));
            Assert.Equal(new byte[] { 120, 45, 80, 234, 213, 209, 121, 220, 145, 132, 4, 173 },
                Convert(s_gray8x3, 8, 3, PixelFormats.Gray8, PixelFormats.Gray4, s_dither));
        }

        [Fact]
        public void Palette_Targets_Should_Match_WIC()
        {
            var halftone8 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone8);

            Assert.Equal(new byte[] { 185, 230, 103, 157, 110, 183 },
                Convert(s_gray8x3, 8, 3, PixelFormats.Gray8, PixelFormats.Indexed2, WithPalette(s_blackRedGreenBlue)));
            Assert.Equal(new byte[] { 153, 8, 144, 136, 137, 128, 153, 120, 128, 153, 9, 136 },
                Convert(s_gray8x3, 8, 3, PixelFormats.Gray8, PixelFormats.Indexed4, WithPalette(halftone8)));
            Assert.Equal(new byte[] { 222, 151, 157, 237, 222, 218, 165, 183 },
                Convert(s_bgr8x4, 8, 4, PixelFormats.Bgr24, PixelFormats.Indexed2, WithPalette(s_blackRedGreenBlue)));
            Assert.Equal(new byte[] { 200, 226, 52, 94, 40, 248, 233, 254, 153, 142, 141, 246, 219, 93, 200, 58 },
                Convert(s_bgr8x4, 8, 4, PixelFormats.Bgr24, PixelFormats.Indexed4, WithPalette(halftone8)));
        }

        [Fact]
        public void Pixels_Below_The_Alpha_Threshold_Should_Diffuse_The_Error_Of_Their_Nearest_Color()
        {
            byte[] pixels =
            {
                237, 191, 136, 100, 95, 3, 173, 255, 41, 171, 20, 255, 86, 231, 216, 100, 86, 121, 26, 0, 67, 32, 196, 0,
                149, 104, 114, 255, 44, 136, 107, 255, 143, 174, 22, 100, 2, 210, 28, 255, 251, 71, 12, 100, 217, 57, 1, 0,
                101, 103, 169, 0, 42, 68, 8, 0, 254, 101, 215, 0, 203, 98, 47, 100, 88, 21, 27, 140, 76, 137, 17, 0,
                206, 121, 82, 0, 187, 44, 177, 0, 187, 231, 107, 255, 101, 9, 194, 140, 0, 221, 57, 100, 113, 227, 138, 140,
                45, 157, 145, 0, 28, 13, 195, 255, 254, 177, 127, 0, 217, 118, 49, 255, 190, 189, 71, 140, 87, 132, 15, 0,
                88, 134, 84, 0, 75, 6, 24, 0,
            };
            var halftone = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone256, addTransparentColor: true);

            Assert.Equal(new byte[]
                {
                    155, 161, 16, 221, 17, 193, 110, 112, 22, 56, 11, 7, 173, 8, 207, 43,
                    33, 17, 79, 166, 126, 193, 56, 157, 144, 192, 151, 47, 86, 13, 81, 33,
                },
                Convert(pixels, 8, 4, PixelFormats.Bgra8888, PixelFormats.Indexed8, WithPalette(halftone)));
            Assert.Equal(new byte[]
                {
                    255, 161, 16, 255, 255, 255, 110, 112, 255, 56, 255, 255, 255, 255, 255, 255,
                    33, 255, 255, 255, 126, 193, 255, 157, 255, 192, 255, 47, 86, 255, 255, 255,
                },
                Convert(pixels, 8, 4, PixelFormats.Bgra8888, PixelFormats.Indexed8, WithPalette(halftone, 50)));
        }
    }
}
