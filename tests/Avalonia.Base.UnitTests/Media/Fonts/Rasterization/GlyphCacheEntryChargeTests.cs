using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// The glyph cache budget charges what an entry keeps besides its pixels: the atlas index
    /// entry of every placed mask, and the objects that hold a cached mask. A probe that filled
    /// an atlas and a mask cache with 10 000 to 100 000 one-pixel entries and compared the heap
    /// after a full collection measured 152 to 218 bytes per atlas index entry (dictionary and
    /// page key list, with their growth slack) and 196 to 202 bytes per cached mask beyond its
    /// <see cref="GlyphMask.ByteCost"/>; the charges must cover at least the lower ends.
    /// </summary>
    public class GlyphCacheEntryChargeTests
    {
        private const long MinAtlasIndexBytes = 150;
        private const long MinMaskEntryBytes = 190;
        private const int Entries = 1000;

        private static GlyphMask MakeMask(int side) => new(new byte[side * side], side, side, 0, 0);

        private static GlyphMaskKey Key(int glyph)
            => new((ushort)glyph, GlyphMaskKey.QuantizeScale(16f), 0, GlyphMaskMode.Antialiased);

        [Fact]
        public void Atlas_Entries_Are_Charged_For_Their_Index_Until_Their_Page_Is_Evicted()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
            var tick = atlas.Tick();

            for (var glyph = 1; glyph <= Entries; glyph++)
            {
                Assert.True(atlas.TryAdd(Key(glyph), MakeMask(3), tick, out _));
            }

            var index = budget.BytesOf(GlyphCachePoolKind.Atlas) - atlas.AllocatedBytes;

            Assert.True(index >= Entries * MinAtlasIndexBytes, $"{Entries} entries charged {index} bytes beside their pages");

            var charged = budget.BytesOf(GlyphCachePoolKind.Atlas);

            Assert.Equal(charged, ((IGlyphCachePool)atlas).EvictOldest(long.MaxValue, long.MaxValue));
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Retired_Atlas_Entries_Release_Their_Index_Charge()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var atlas = new GlyphMaskAtlas(budget);
            var tick = atlas.Tick();

            for (var glyph = 1; glyph <= Entries; glyph++)
            {
                Assert.True(atlas.TryAdd(1, Key(glyph), GlyphMaskAtlas.Uncorrected, MakeMask(3), tick, out _));
                Assert.True(atlas.TryAdd(2, Key(glyph), GlyphMaskAtlas.Uncorrected, MakeMask(3), tick, out _));
            }

            var before = budget.BytesOf(GlyphCachePoolKind.Atlas);

            atlas.Retire(1);

            // The page still holds the other owner's entries, so only the index charge goes.
            Assert.True(before - budget.BytesOf(GlyphCachePoolKind.Atlas) >= Entries * MinAtlasIndexBytes,
                $"retiring {Entries} entries released {before - budget.BytesOf(GlyphCachePoolKind.Atlas)} bytes");

            atlas.Release();

            Assert.Equal(0, budget.UsedBytes);
        }

        [Fact]
        public void Cached_Masks_Are_Charged_For_Their_Objects_Until_Evicted_Or_Cleared()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var cache = new GlyphMaskCache(budget);
            long pixels = 0;

            for (var glyph = 1; glyph <= Entries; glyph++)
            {
                pixels += cache.GetOrBuild(Key(glyph), static key => MakeMask(1 + key.Glyph % 7)).ByteCost;
            }

            var objects = budget.BytesOf(GlyphCachePoolKind.Masks) - pixels;

            Assert.True(objects >= Entries * MinMaskEntryBytes, $"{Entries} masks charged {objects} bytes beside their ByteCost");
            Assert.Equal(cache.TotalCost, budget.BytesOf(GlyphCachePoolKind.Masks));

            var charged = budget.BytesOf(GlyphCachePoolKind.Masks);

            var freed = ((IGlyphCachePool)cache).EvictOldest(long.MaxValue, charged / 2);

            Assert.True(freed >= charged / 2, $"evicting half of {charged} bytes freed {freed}");
            Assert.Equal(cache.TotalCost, budget.BytesOf(GlyphCachePoolKind.Masks));

            cache.Clear();

            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Masks));
            Assert.Equal(0, cache.TotalCost);
        }
    }
}
