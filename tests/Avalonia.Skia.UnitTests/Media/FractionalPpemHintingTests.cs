using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Bytecode hinting at fractional sizes. A font that sets head.flags bit 3 ("force ppem to
    /// integer values") has programs written for whole pixel sizes; FreeType then hints at the
    /// rounded ppem with the scale recomputed from it (tt_size_reset), so a request for 15.375 px
    /// hints exactly like 15 px and 15.5 px like 16 px. The expected values are FreeType 2.13.2's
    /// v40 outlines for the shipped fonts (bit 3 honoured), on-curve extremes in 26.6 pixels.
    /// </summary>
    public class FractionalPpemHintingTests : IDisposable
    {
        // The application every test of the class draws text in.
        private readonly IDisposable _application = TextTestApplication.Enter();

        public void Dispose() => _application.Dispose();

        [Theory]
        [InlineData("msyh.ttc", 0, '\u9F8D', 120, 30720, 12, 946, -69, 768)]
        [InlineData("msyh.ttc", 0, '\u9F8D', 123, 30720, 12, 946, -69, 768)]
        [InlineData("msyh.ttc", 0, '\u9F8D', 128, 32768, 13, 1009, -73, 832)]
        [InlineData("msyh.ttc", 0, '\u9F8D', 124, 32768, 13, 1009, -73, 832)]
        [InlineData("segoeui.ttf", 0, 'a', 123, 30720, 42, 421, 0, 512)]
        [InlineData("arial.ttf", 0, 'g', 123, 30720, 31, 470, -192, 512)]
        public void Integer_Ppem_Fonts_Hint_At_The_Rounded_Ppem(string fontFile, int faceIndex, char character,
            int scaleQ, int expectedScale, int expectedLeft, int expectedRight, int expectedBottom, int expectedTop)
        {
            var typeface = LoadSystemFont(fontFile, faceIndex);
            var glyph = typeface.CharacterToGlyphMap[character];
            var hinter = typeface.GetTrueTypeHinter((ushort)scaleQ, GlyphMaskMode.Antialiased);

            Assert.NotNull(hinter);
            Assert.Equal(expectedScale, hinter!.State.Scale);

            var rented = hinter.Rent();

            try
            {
                Assert.True(rented.TryHint(glyph, backwardCompatibility: 4));

                var (left, right, bottom, top) = OnCurveExtremes(rented.Zone!);

                Assert.Equal((expectedLeft, expectedRight, expectedBottom, expectedTop), (left, right, bottom, top));
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

        private static (int Left, int Right, int Bottom, int Top) OnCurveExtremes(
            Avalonia.Media.Fonts.Rasterization.TrueType.TrueTypeZone zone)
        {
            int left = int.MaxValue, right = int.MinValue, bottom = int.MaxValue, top = int.MinValue;

            for (var i = 0; i < zone.PointCount - 4; i++)
            {
                if ((zone.Tags[i] & 1) != 0)
                {
                    left = Math.Min(left, zone.CurX[i]);
                    right = Math.Max(right, zone.CurX[i]);
                    bottom = Math.Min(bottom, zone.CurY[i]);
                    top = Math.Max(top, zone.CurY[i]);
                }
            }

            return (left, right, bottom, top);
        }
    }
}
