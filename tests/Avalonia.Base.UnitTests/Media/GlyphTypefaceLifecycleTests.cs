using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    /// <summary>
    /// <see cref="Avalonia.Media.GlyphTypeface.Dispose"/> unlinks the per-instance glyph cache, so
    /// its bytes leave the glyph cache budget at once, without disposing any payload: payloads are
    /// handed out lock-free and can escape into retained compositor render data that outlives the
    /// typeface, so disposing them would risk a use-after-free.
    /// </summary>
    public class GlyphTypefaceLifecycleTests
    {
        [Fact]
        public void Dispose_Unlinks_The_Glyph_Cache_Without_Disposing_Its_Payloads()
        {
            // A CFF font: its ink-bounds path interprets the charstring into a box and populates the
            // glyph cache without needing a render backend.
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.CffTest).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap['I'];
            Assert.True(typeface.TryGetGlyphMetrics(glyph, out _));

            // The cache now holds at least one entry.
            var cache = typeface.GlyphCache;
            Assert.NotNull(cache);
            Assert.True(cache!.Count > 0);

            var entry = cache.GetEntry(glyph);

            typeface.Dispose();

            // The typeface lets go of the cache and the cache of its entries; an entry handed out
            // before disposal keeps what it held.
            Assert.Null(typeface.GlyphCache);
            Assert.Equal(0, cache.Count);
            Assert.True(entry.HasBounds);
        }
    }
}
