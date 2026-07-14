using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class SlugTexelStoreTests
    {
        [Fact]
        public void Placements_Are_Stable_And_The_Version_Counts_Appends()
        {
            var typeface = LoadTypeface();
            var store = typeface.SlugStore;
            var g = typeface.CharacterToGlyphMap['g'];
            var o = typeface.CharacterToGlyphMap['o'];

            Assert.True(store.TryRealize(typeface, g, out var first));
            Assert.Equal(1, store.Version);

            Assert.True(store.TryRealize(typeface, o, out _));
            Assert.Equal(2, store.Version);

            // Re-realization is a lookup: same placement, no new texels.
            Assert.True(store.TryRealize(typeface, g, out var again));
            Assert.Equal(2, store.Version);
            Assert.Equal(first.GlyphLocX, again.GlyphLocX);
            Assert.Equal(first.GlyphLocY, again.GlyphLocY);

            // The placement carries the em bounds the draw rect needs; 'g' has a descender.
            Assert.True(first.MinX < first.MaxX);
            Assert.True(first.MinY < 0);
            Assert.True(first.MaxY > 0);
            Assert.True(store.CurveRowCount >= 1);
            Assert.True(store.BandRowCount >= 1);
        }

        [Fact]
        public void Whitespace_And_Unwalkable_Glyphs_Realize_As_Empty()
        {
            var typeface = LoadTypeface();
            var store = typeface.SlugStore;

            // Space: walked, no contours — realized as the empty placement, nothing appended,
            // so runs containing spaces stay on the Slug path.
            Assert.True(store.TryRealize(typeface, typeface.CharacterToGlyphMap[' '], out var space));
            Assert.Equal(0, space.HorizontalBandCount);
            Assert.Equal(0, store.Version);

            // Out-of-range glyph: the walker rejects it, and every other path draws nothing
            // for it too, so it also realizes empty rather than knocking the run off the tier.
            Assert.True(store.TryRealize(typeface, (ushort)typeface.GlyphCount, out var bogus));
            Assert.Equal(0, bogus.HorizontalBandCount);
            Assert.Equal(0, store.Version);
        }

        private static GlyphTypeface LoadTypeface()
        {
            var bytes = LoadFontBytes("Inter-Regular.ttf");
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
