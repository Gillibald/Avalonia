using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// A disposed typeface draws no more, so what its glyph caches hold is given back at once
    /// rather than when the GC collects them.
    /// </summary>
    public class GlyphCacheDisposalTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Disposing_A_Typeface_Credits_Its_Cache_Bytes_At_Once()
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();
            var scratch = new GlyphPathBuilder();

            typeface.CacheBudget = budget;

            using (budget.BeginFrame())
            {
                foreach (var c in "Hamburgefonstiv 0123456789")
                {
                    var key = new GlyphMaskKey(typeface.CharacterToGlyphMap[c], GlyphMaskKey.QuantizeScale(18f), 0,
                        GlyphMaskMode.Antialiased);
                    var mask = typeface.MaskCache.GetOrBuild(key, k => GlyphMasks.Build(typeface, scratch, k));

                    typeface.MaskAtlas.TryAdd(key, mask, typeface.MaskAtlas.Tick(), out _);
                }

                typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(18f), GlyphMaskMode.Antialiased);
            }

            Assert.True(budget.BytesOf(GlyphCachePoolKind.Masks) > 0);
            Assert.True(budget.BytesOf(GlyphCachePoolKind.Atlas) > 0);
            Assert.True(budget.BytesOf(GlyphCachePoolKind.Hinters) > 0);

            typeface.Dispose();

            Assert.Equal(0, budget.UsedBytes);
        }
    }
}
