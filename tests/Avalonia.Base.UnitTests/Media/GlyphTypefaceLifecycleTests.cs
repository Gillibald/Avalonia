using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    /// <summary>
    /// <see cref="Avalonia.Media.GlyphTypeface.Dispose"/> releases the per-instance glyph cache so its
    /// entries (and, for outline glyphs, native geometry) are not held until the typeface itself is
    /// collected.
    /// </summary>
    public class GlyphTypefaceLifecycleTests
    {
        [Fact]
        public void Dispose_Releases_The_Glyph_Cache()
        {
            // A CFF font: its ink-bounds path interprets the charstring into a box and populates the
            // glyph cache without needing a render backend.
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.CffTest).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap['I'];
            Assert.True(typeface.TryGetGlyphMetrics(glyph, out _));

            // The cache now holds at least one entry.
            Assert.NotNull(typeface.GlyphCache);
            Assert.True(typeface.GlyphCache!.Count > 0);

            typeface.Dispose();

            // Dispose drops the cache reference, so its retained geometry is no longer held by the
            // (disposed) typeface.
            Assert.Null(typeface.GlyphCache);
        }
    }
}
