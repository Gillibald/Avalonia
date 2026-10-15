using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class LightHintingParityTests : IDisposable
    {
        // The application every test of the class draws text in.
        private readonly IDisposable _application = TextTestApplication.Enter();

        public void Dispose() => _application.Dispose();

        /// <summary>
        /// Light hints like FreeType's v40 interpreter, whose IUP derives the interpolation
        /// scale with FT_DivFix and applies it with FT_MulFix, both rounding to nearest. The
        /// expected values are FreeType 2.13.2's v40 outline of each glyph, every point as
        /// (x, y) in 26.6. Segoe UI 'a' at 12 px has an x interpolated by IUP[x], Arial 'g' at
        /// 18 px a y interpolated by IUP[y], each landing 1/64 px off with a truncated scale.
        /// Arial '&amp;' at 13 px sets the projection perpendicular to a diagonal, reads it back
        /// with GPV into a control value and moves a stem by it, so the unit vector has to be
        /// FreeType's truncated (12799, 10227), not the rounded (12800, 10228).
        /// </summary>
        [Theory]
        [InlineData("segoeui.ttf", 'a', 12, new[]
        {
            337, 0, 276, 0, 276, 81, 275, 81, 234, 0, 156, 0, 99, 0, 34, 62, 34, 113, 34, 222,
            161, 240, 276, 256, 276, 320, 197, 320, 127, 320, 71, 320, 71, 361, 128, 384, 202, 384,
            337, 384, 337, 244, 276, 192, 183, 181, 141, 176, 97, 150, 97, 118, 97, 94, 137, 64,
            171, 64, 216, 64, 276, 118, 276, 160
        })]
        [InlineData("arial.ttf", 'g', 18, new[]
        {
            57, -64, 156, -79, 162, -126, 190, -147, 228, -176, 293, -176, 363, -176, 440, -116,
            453, -63, 461, -30, 461, 75, 394, 0, 295, 0, 172, 0, 37, 186, 37, 316, 37, 406,
            99, 558, 217, 640, 296, 640, 402, 640, 470, 558, 470, 640, 564, 640, 564, 89,
            564, -60, 507, -184, 384, -256, 294, -256, 187, -256, 55, -160, 141, 324, 141, 197,
            235, 80, 305, 80, 375, 80, 469, 196, 469, 321, 469, 439, 372, 560, 303, 560,
            236, 560, 141, 441
        })]
        [InlineData("arial.ttf", '&', 13, new[]
        {
            395, 78, 359, 39, 274, 0, 224, 0, 133, 0, 79, 60, 36, 109, 36, 169, 36, 223, 107, 309,
            178, 342, 138, 394, 111, 460, 111, 490, 111, 550, 195, 640, 259, 640, 320, 640, 397, 554, 397, 494,
            397, 397, 284, 328, 391, 191, 410, 227, 420, 274, 496, 258, 476, 181, 443, 137, 484, 87, 536, 53,
            487, 0, 442, 26, 247, 363, 294, 399, 322, 454, 322, 487, 322, 526, 284, 576, 255, 576, 225, 576,
            186, 527, 186, 491, 186, 473, 200, 434, 214, 412, 349, 128, 215, 295, 156, 261, 115, 203, 115, 174,
            115, 139, 172, 64, 225, 64, 258, 64, 328, 100
        })]
        public void Light_Hinting_Matches_FreeType_V40(string fontFile, char character, int pixelsPerEm, int[] expected)
        {
            var typeface = LoadSystemFont(fontFile, 0);
            var glyph = typeface.CharacterToGlyphMap[character];
            var hinter = typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(pixelsPerEm), GlyphMaskMode.Antialiased);

            Assert.NotNull(hinter);

            var rented = hinter!.Rent();

            try
            {
                Assert.True(rented.TryHint(glyph, backwardCompatibility: 4));

                var zone = rented.Zone!;
                var actual = new int[(zone.PointCount - 4) * 2];

                for (var i = 0; i < zone.PointCount - 4; i++)
                {
                    actual[i * 2] = zone.CurX[i];
                    actual[i * 2 + 1] = zone.CurY[i];
                }

                Assert.Equal(expected, actual);
            }
            finally
            {
                hinter.Return(rented);
            }
        }

        private static GlyphTypeface LoadSystemFont(string fontFile, int faceIndex)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped fonts.");

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fontFile);

            Assert.SkipWhen(!File.Exists(path), $"{fontFile} is not installed.");

            return TestGlyphTypefaces.FromBytes(File.ReadAllBytes(path), faceIndex);
        }
    }
}
