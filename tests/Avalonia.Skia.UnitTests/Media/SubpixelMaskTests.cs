using System;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Subpixel (LCD) glyph mask generation: three filtered stripe channels per final pixel,
    /// geometry-agnostic RGB order, energy matching the grayscale mask of the same glyph.
    /// </summary>
    public class SubpixelMaskTests
    {
        [Fact]
        public void A_Subpixel_Mask_Has_Three_Channels_And_The_Wider_Apron()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['H'];
            var scratch = new GlyphPathBuilder();

            var gray = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(32), 0, GlyphMaskMode.Antialiased));
            var lcd = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(32), 0, GlyphMaskMode.Subpixel));

            Assert.Equal(1, gray.Channels);
            Assert.Equal(3, lcd.Channels);
            Assert.Equal(lcd.Width * 3 * lcd.Height, lcd.Alpha.Length);

            // Same ink box, one extra apron pixel each side horizontally, same vertical apron.
            Assert.Equal(gray.Width + 2 * (GlyphMasks.SubpixelApron - GlyphMasks.Apron), lcd.Width);
            Assert.Equal(gray.Height, lcd.Height);
            Assert.Equal(gray.Left - (GlyphMasks.SubpixelApron - GlyphMasks.Apron), lcd.Left);
            Assert.Equal(gray.Top, lcd.Top);
        }

        [Fact]
        public void Channel_Energy_Tracks_The_Grayscale_Mask()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['o'];
            var scratch = new GlyphPathBuilder();

            var gray = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(24), 0, GlyphMaskMode.Antialiased));
            var lcd = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(24), 0, GlyphMaskMode.Subpixel));

            // The filter conserves energy and 3x sampling measures the same ink integral, so
            // per-channel coverage must track total grayscale coverage. The bound is loose
            // enough for the filter's round-half-up bias across 3x the samples (~3-4% on a
            // small curved glyph) while still catching structural errors like a lost channel.
            var graySum = gray.Alpha.Sum(a => (long)a);
            var lcdSum = lcd.Alpha.Sum(a => (long)a) / 3.0;

            Assert.True(Math.Abs(lcdSum - graySum) <= graySum * 0.05,
                $"grayscale energy {graySum}, subpixel per-channel energy {lcdSum:0}");
        }

        [Fact]
        public void Solid_Interiors_Are_Fully_Covered_In_Every_Channel()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['H'];
            var scratch = new GlyphPathBuilder();

            var lcd = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(64), 0, GlyphMaskMode.Subpixel));

            var solidPixels = 0;

            for (var y = 0; y < lcd.Height; y++)
            {
                for (var x = 0; x < lcd.Width; x++)
                {
                    var i = (y * lcd.Width + x) * 3;

                    if (lcd.Alpha[i] == 255 && lcd.Alpha[i + 1] == 255 && lcd.Alpha[i + 2] == 255)
                    {
                        solidPixels++;
                    }
                }
            }

            // A 64px H has a large solid interior; the filter of an all-covered neighborhood
            // must stay exactly 255 (normalization is exact by construction).
            Assert.True(solidPixels > 200, $"only {solidPixels} fully covered pixels");
        }

        [Fact]
        public void Byte_Cost_Follows_The_Three_Channel_Payload()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['H'];
            var scratch = new GlyphPathBuilder();

            var lcd = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(32), 0, GlyphMaskMode.Subpixel));

            // The cache budget counts actual payload bytes, so subpixel entries weigh three
            // channels — capacity shrinks instead of the budget silently inflating.
            Assert.Equal(lcd.Width * lcd.Height * 3 + 48, lcd.ByteCost);
        }

        [Fact]
        public void Stripe_Edges_Ramp_Across_Channels()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['H'];
            var scratch = new GlyphPathBuilder();

            var lcd = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(48), 0, GlyphMaskMode.Subpixel));

            // On the left edge of a stem, coverage arrives rightmost-channel-first as the
            // stripe order marches into the ink: find a row crossing the left stem and assert
            // some pixel carries ascending channel coverage (r <= g <= b with a real spread).
            var row = lcd.Height / 2;
            var found = false;

            for (var x = 0; x < lcd.Width - 1 && !found; x++)
            {
                var i = (row * lcd.Width + x) * 3;
                var r = lcd.Alpha[i];
                var g = lcd.Alpha[i + 1];
                var b = lcd.Alpha[i + 2];

                found = r < g && g < b && b - r >= 32;
            }

            Assert.True(found, "no ascending stripe ramp found on the leading edge");
        }

        private static GlyphTypeface LoadTypeface()
        {
            var bytes = File.ReadAllBytes(Path.Combine(FindTests(), "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf"));
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
        }

        private static string FindTests()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return directory!.FullName;
        }
    }
}
