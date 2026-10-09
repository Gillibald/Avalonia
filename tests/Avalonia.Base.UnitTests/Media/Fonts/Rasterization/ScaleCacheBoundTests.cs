using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// A zoom animation asks a typeface for a new mask scale every frame. What the typeface keeps
    /// per scale (zone warps, bytecode hinters) must stay bounded however many scales it is asked
    /// for, while the scales in use stay cached. Hinters are bounded by the glyph cache budget,
    /// zone warps by a count of their own.
    /// </summary>
    public class ScaleCacheBoundTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void A_Zoom_Through_Thousands_Of_Scales_Keeps_A_Bounded_Number_Of_Warps_And_Hinters()
        {
            var typeface = SyntheticFont.FromAsset(NotoMonoUri).CreateGlyphTypeface();
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            typeface.CacheBudget = budget;

            Assert.True(typeface.HasTrueTypeHinting);

            budget.SetLimit(16 * typeface.TrueTypeHinterBytes);

            // 6 to 200 px in steps of a tenth, one frame each: about two thousand distinct
            // quantized scales.
            for (var size = 6.0f; size < 200f; size += 0.1f)
            {
                using (budget.BeginFrame())
                {
                    var scaleQ = GlyphMaskKey.QuantizeScale(size);

                    typeface.GridFit.GetWarp(scaleQ);
                    typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);
                }
            }

            Assert.InRange(typeface.GridFit.CachedWarpCount, 1, 512);
            Assert.InRange(typeface.TrueTypeHinterCount, 1, 16 * 3 / 2 + 1);

            // The scale in use stays cached: asking again returns the same hinter.
            var current = GlyphMaskKey.QuantizeScale(13f);
            var hinter = typeface.GetTrueTypeHinter(current, GlyphMaskMode.Antialiased);

            Assert.Same(hinter, typeface.GetTrueTypeHinter(current, GlyphMaskMode.Antialiased));
        }
    }
}
