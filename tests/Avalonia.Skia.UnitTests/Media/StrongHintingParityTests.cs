using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class StrongHintingParityTests
    {
        /// <summary>
        /// Strong runs the font's complete program for y, the classic engine FreeType ships as
        /// v35. Arial's programs branch on the engine version GETINFO reports and round its
        /// crossbar and x-height differently for the v40 answer, so Strong y only matches the
        /// classic engine when the programs are told they run on it. The expected values are
        /// FreeType 2.13.2's v35 y coordinates of Arial 'e' at 22 px, every outline point.
        /// </summary>
        [Fact]
        public void Strong_Hinting_Y_Matches_FreeType_V35()
        {
            var typeface = LoadSystemFont("arial.ttf", 0);
            var glyph = typeface.CharacterToGlyphMap['e'];
            var hinter = typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(22), GlyphMaskMode.Antialiased, strong: true);

            Assert.NotNull(hinter);

            int[] expected =
            {
                256, 256, 134, 0, 0, 16, 199, 378, 563, 768, 768, 768, 583, 415, 404,
                384, 384, 260, 128, 128, 128, 189, 512, 512, 568, 596, 640, 640, 640, 570
            };

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
