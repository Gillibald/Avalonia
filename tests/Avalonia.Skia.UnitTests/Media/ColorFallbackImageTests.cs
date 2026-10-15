using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Transformed colour glyphs drawn where the surface cannot be written directly (under an
    /// opacity, a layer or a turned clip) draw backend images of their colour masks. Those images
    /// belong to the typeface, keyed like the masks, so every run that shows the same glyph under
    /// the same transform draws the same image: a cell whose text changes makes no image for an
    /// emoji another cell already shows. They are charged to the glyph cache budget and go with
    /// its trims and the typeface.
    /// </summary>
    public class ColorFallbackImageTests
    {
        private const int Width = 320;
        private const int Height = 200;
        private const string AppleColorEmojiPath = "/System/Library/Fonts/Apple Color Emoji.ttc";

        public enum Font
        {
            Cbdt,
            AppleColorEmoji,
        }

        [Theory]
        [InlineData(Font.Cbdt)]
        [InlineData(Font.AppleColorEmoji)]
        public void A_New_Run_Of_Colour_Glyphs_Another_Run_Showed_Under_The_Transform_Makes_No_Image(Font font)
        {
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = CreateTypeface(font, out var glyphs);
            var first = CreateRun(typeface, glyphs);
            var second = CreateRun(typeface, glyphs);

            var images = GlyphRasterDiagnostics.ColorFallbackImagesOnThread;
            var firstPixels = Render(first);
            var made = GlyphRasterDiagnostics.ColorFallbackImagesOnThread - images;

            images = GlyphRasterDiagnostics.ColorFallbackImagesOnThread;
            var secondPixels = Render(second);
            var madeAgain = GlyphRasterDiagnostics.ColorFallbackImagesOnThread - images;

            Assert.Equal(glyphs.Distinct().Count(), made);
            Assert.Equal(0, madeAgain);
            TransformedAtlasTests.AssertEqual(firstPixels, secondPixels, "second run");
            GC.KeepAlive(first);
            GC.KeepAlive(second);
        }

        [Fact]
        public void Shared_Colour_Images_Are_Charged_And_Go_With_A_Trim_And_The_Typeface()
        {
            using var scope = ColorAtlasTests.CreateEnvironment();
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var typeface = CreateTypeface(Font.Cbdt, out var glyphs);

            typeface.CacheBudget = budget;

            var run = CreateRun(typeface, glyphs);

            Render(run);

            Assert.True(budget.BytesOf(GlyphCachePoolKind.ColorFallbackImages) > 0, "no colour image was charged");

            // Memory pressure with no frame open drops everything, the shared images included.
            budget.TrimForMemoryPressure();

            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.ColorFallbackImages));

            // Drawn again they are made again, and released with the typeface.
            var images = GlyphRasterDiagnostics.ColorFallbackImagesOnThread;

            Render(CreateRun(typeface, glyphs));

            Assert.Equal(1, GlyphRasterDiagnostics.ColorFallbackImagesOnThread - images);
            Assert.True(budget.BytesOf(GlyphCachePoolKind.ColorFallbackImages) > 0);

            typeface.Dispose();

            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.ColorFallbackImages));
            GC.KeepAlive(run);
        }

        private static GlyphTypeface CreateTypeface(Font font, out ushort[] glyphs)
        {
            if (font == Font.Cbdt)
            {
                var typeface = BackendBitmapStrikeTests.CreateTwoStrikeTypeface(out var glyph);

                glyphs = new[] { glyph };
                return typeface;
            }

            Assert.SkipWhen(!File.Exists(AppleColorEmojiPath), "Apple Color Emoji is macOS only");

            var emoji = TestGlyphTypefaces.FromBytes(File.ReadAllBytes(AppleColorEmojiPath));

            glyphs = new[] { 0x1F600, 0x2764, 0x1F600 }.Select(c => emoji.CharacterToGlyphMap[c]).ToArray();

            return emoji;
        }

        private static GlyphRun CreateRun(GlyphTypeface typeface, ushort[] glyphs)
            => new(typeface, 28, default, glyphs.Select((g, i) => new GlyphInfo(g, i, 34)).ToList(), new Point(0, 0));

        /// <summary>
        /// Draws <paramref name="run"/> turned by 17 degrees under an opacity, which keeps the draw
        /// from writing the surface directly, on a raster surface, and returns the pixels.
        /// </summary>
        private static byte[] Render(GlyphRun run)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            {
                impl.PushOpacity(0.8, null);
                impl.Transform = Matrix.CreateRotation(17 * Math.PI / 180) * Matrix.CreateTranslation(60, 80);
                impl.DrawGlyphRun(Brushes.Black, run.PlatformImpl.Item);
                impl.Transform = Matrix.Identity;
                impl.PopOpacity();
            }

            return bitmap.GetPixelSpan().ToArray();
        }
    }
}
