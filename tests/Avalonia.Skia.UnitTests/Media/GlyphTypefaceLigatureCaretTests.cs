using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class GlyphTypefaceLigatureCaretTests
    {
        private const string NotoSansItalic = "Avalonia.Skia.UnitTests.Assets.NotoSans-Italic.ttf";
        private const string NotoSansHebrew = "Avalonia.Skia.UnitTests.Assets.NotoSansHebrew-Regular.ttf";
        private const string DejaVuSans = "Avalonia.Skia.UnitTests.Fonts.DejaVuSans.ttf";
        private const string Manrope = "Avalonia.Skia.UnitTests.Fonts.Manrope-Light.ttf";

        [Fact]
        public void Shaped_Ffi_Ligature_Should_Return_The_Font_Carets()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var shapedBuffer = TextShaper.Current.ShapeText("ffi",
                    new TextShaperOptions(glyphTypeface, 12, 0, CultureInfo.InvariantCulture));

                Assert.Equal(1, shapedBuffer.Length);

                Span<int> carets = stackalloc int[4];

                var count = glyphTypeface.GetLigatureCaretPositions(shapedBuffer[0].GlyphIndex, carets);

                Assert.Equal(2, count);
                Assert.Equal(new[] { 247, 557 }, carets.Slice(0, count).ToArray());
            }
        }

        [Fact]
        public void Ligature_Without_Carets_In_A_Font_With_A_LigCaretList_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var shapedBuffer = TextShaper.Current.ShapeText("ff",
                    new TextShaperOptions(glyphTypeface, 12, 0, CultureInfo.InvariantCulture));

                Assert.Equal(1, shapedBuffer.Length);

                Assert.Equal(0, glyphTypeface.GetLigatureCaretPositions(shapedBuffer[0].GlyphIndex, stackalloc int[4]));
            }
        }

        [Fact]
        public void Ligature_Of_A_Font_With_An_Empty_LigCaretList_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(DejaVuSans);

                Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(0xFB03, out var ffi));

                Assert.Equal(0, glyphTypeface.GetLigatureCaretPositions(ffi, stackalloc int[4]));
            }
        }

        [Fact]
        public void Hebrew_Alef_Lamed_Should_Return_The_Font_Caret()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansHebrew);

                Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(0xFB4F, out var alefLamed));

                Span<int> carets = stackalloc int[4];

                var count = glyphTypeface.GetLigatureCaretPositions(alefLamed, carets);

                Assert.Equal(1, count);
                Assert.Equal(299, carets[0]);
            }
        }

        [Fact]
        public void Font_Without_A_GDEF_Table_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(Manrope);

                var shapedBuffer = TextShaper.Current.ShapeText("ff",
                    new TextShaperOptions(glyphTypeface, 12, 0, CultureInfo.InvariantCulture));

                Assert.Equal(0, glyphTypeface.GetLigatureCaretPositions(shapedBuffer[0].GlyphIndex, stackalloc int[4]));
            }
        }

        private static GlyphTypeface Load(string resourceName)
        {
            var loader = AvaloniaLocator.Current.GetRequiredService<Platform.IAssetLoader>();

            using var stream = loader.Open(new Uri($"resm:{resourceName}?assembly=Avalonia.Skia.UnitTests", UriKind.Absolute));

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face);
        }

        private static IDisposable Start()
        {
            return UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface()));
        }
    }
}
