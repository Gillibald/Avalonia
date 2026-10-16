using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// A typeface used outside any application (font tooling, ink bounds, outlines, strike decode)
    /// keeps its glyph caches in a budget of its own, released with it.
    /// </summary>
    public class StandaloneGlyphTypefaceTests
    {
        [Fact]
        public void A_Typeface_Without_An_Application_Measures_And_Rasterizes_Glyphs()
        {
            var typeface = LoadInter();

            try
            {
                var glyph = typeface.CharacterToGlyphMap['A'];

                Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));
                Assert.True(box.XMax > box.XMin);

                var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(),
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(16), 0, GlyphMaskMode.Antialiased, false, false));

                Assert.False(mask.IsEmpty);
            }
            finally
            {
                typeface.Dispose();
            }
        }

        [Fact]
        public void The_Budget_Of_A_Typeface_Without_An_Application_Is_Released_With_It()
        {
            var typeface = LoadInter();

            typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap['A'], out _);

            var budget = typeface.CacheBudget;

            typeface.Dispose();

            Assert.True(budget.IsDisposed);
        }

        private static GlyphTypeface LoadInter()
        {
            using var stream = SfntFaceTestHelper.OpenAsset(FontFileFixture.InterRegularUri);

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face);
        }
    }
}
