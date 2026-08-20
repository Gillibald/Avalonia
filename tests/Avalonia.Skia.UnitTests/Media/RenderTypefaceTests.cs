using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class RenderTypefaceTests
    {
        private const string InterFontUri = "resm:Avalonia.Skia.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Skia.UnitTests";

        [Fact]
        public void CreateTypeface_Should_Create_SkiaTypeface_From_Font_Data()
        {
            var glyphTypeface = LoadGlyphTypeface(InterFontUri);

            var renderInterface = new PlatformRenderInterface();

            var platformTypeface = renderInterface.CreateTypeface(glyphTypeface);

            var skiaTypeface = Assert.IsType<SkiaTypeface>(platformTypeface);

            Assert.Equal("Inter", skiaTypeface.FamilyName);

            // Directly created typefaces are not cached on the glyph typeface.
            platformTypeface.Dispose();
            glyphTypeface.Dispose();
        }

        [Fact]
        public void CreateTypeface_Should_Apply_Font_Simulations()
        {
            var glyphTypeface = LoadGlyphTypeface(InterFontUri, FontSimulations.Bold | FontSimulations.Oblique);

            var renderInterface = new PlatformRenderInterface();

            var platformTypeface = renderInterface.CreateTypeface(glyphTypeface);

            var skiaTypeface = Assert.IsType<SkiaTypeface>(platformTypeface);

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, skiaTypeface.FontSimulations);

            using (var skFont = skiaTypeface.CreateSKFont(16))
            {
                Assert.True(skFont.Embolden);
                Assert.True(skFont.SkewX < 0);
            }

            platformTypeface.Dispose();
            glyphTypeface.Dispose();
        }

        [Fact]
        public void PlatformTypeface_Should_Be_Created_By_Render_Interface()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: new PlatformRenderInterface())))
            {
                var glyphTypeface = LoadGlyphTypeface(InterFontUri);

                var platformTypeface = glyphTypeface.PlatformTypeface;

                Assert.IsType<SkiaTypeface>(platformTypeface);
                Assert.Equal("Inter", platformTypeface.FamilyName);
                Assert.Same(platformTypeface, glyphTypeface.PlatformTypeface);

                glyphTypeface.Dispose();
            }
        }

        [Win32Fact("Requires the Segoe UI Emoji font file")]
        public void Color_Emoji_Should_Rasterize_In_Color_Through_Render_Typeface()
        {
            const int emojiCodepoint = 0x1F600;

            // Load the system color emoji font by path: this exercises the memory-mapped loader
            // and proves color glyph rasterization survives the FromData path.
            var fontPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "seguiemj.ttf");

            Assert.True(SfntFace.TryLoad(fontPath, 0, out var face));

            var glyphTypeface = new GlyphTypeface(face);

            Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(emojiCodepoint, out _));

            var renderInterface = new PlatformRenderInterface();

            var skiaTypeface = Assert.IsType<SkiaTypeface>(renderInterface.CreateTypeface(glyphTypeface));

            using (var skFont = skiaTypeface.CreateSKFont(48))
            using (var bitmap = new SKBitmap(64, 64))
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawText(char.ConvertFromUtf32(emojiCodepoint), 4, 52, skFont, paint);
                canvas.Flush();

                Assert.True(HasColoredPixel(bitmap), "Expected the emoji to rasterize with colored pixels.");
            }

            skiaTypeface.Dispose();
            glyphTypeface.Dispose();
        }

        private static bool HasColoredPixel(SKBitmap bitmap)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    if (pixel.Alpha > 0 &&
                        (Math.Abs(pixel.Red - pixel.Green) > 16 || Math.Abs(pixel.Green - pixel.Blue) > 16))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static GlyphTypeface LoadGlyphTypeface(string uri, FontSimulations fontSimulations = FontSimulations.None)
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(uri));

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face, fontSimulations);
        }
    }
}
