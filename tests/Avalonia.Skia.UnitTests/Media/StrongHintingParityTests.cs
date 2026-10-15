using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class StrongHintingParityTests : IDisposable
    {
        // The application every test of the class draws text in.
        private readonly IDisposable _application = TextTestApplication.Enter();

        public void Dispose() => _application.Dispose();

        /// <summary>
        /// Strong runs the font's complete program for y, the classic engine FreeType ships as
        /// v35. Arial's programs branch on the engine version GETINFO reports and round its
        /// crossbar and x-height differently for the v40 answer, so Strong y only matches the
        /// classic engine when the programs are told they run on it. The expected values are
        /// FreeType 2.13.2's v35 y coordinates of every outline point: Arial 'e' at 22 px, and
        /// Arial '&amp;' at 13 px, whose program moves a stem by a control value read back from a
        /// projection vector perpendicular to a diagonal (FreeType's truncated unit vector).
        /// </summary>
        [Theory]
        [InlineData('e', 22, new[]
        {
            256, 256, 134, 0, 0, 16, 199, 378, 563, 768, 768, 768, 583, 415, 404,
            384, 384, 260, 128, 128, 128, 189, 512, 512, 568, 596, 640, 640, 640, 570
        })]
        [InlineData('&', 13, new[]
        {
            83, 41, 0, 0, 0, 60, 109, 169, 223, 309, 342, 394, 460, 490, 550,
            640, 640, 640, 551, 489, 388, 316, 116, 216, 263, 247, 165, 101, 64, 39,
            0, 28, 363, 399, 454, 487, 526, 576, 576, 576, 527, 491, 473, 434, 412,
            128, 295, 261, 203, 174, 139, 64, 64, 64, 100
        })]
        public void Strong_Hinting_Y_Matches_FreeType_V35(char character, int pixelsPerEm, int[] expected)
        {
            var typeface = LoadSystemFont("arial.ttf", 0);
            var glyph = typeface.CharacterToGlyphMap[character];
            var hinter = typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(pixelsPerEm), GlyphMaskMode.Antialiased, strong: true);

            Assert.NotNull(hinter);

            var rented = hinter!.Rent();

            try
            {
                Assert.True(rented.TryHintVertically(glyph));

                var zone = rented.Zone!;

                Assert.Equal(expected.Length, zone.PointCount - 4);
                Assert.Equal(expected, zone.CurY.AsSpan(0, expected.Length).ToArray());
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
