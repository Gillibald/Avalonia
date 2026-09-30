using Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Simulated bold and oblique are applied to the fitted outline, so a simulated variant
    /// hints exactly like its source face and shares the source's simulation-independent
    /// rasterization state.
    /// </summary>
    public class SimulatedVariantRasterizationTests
    {
        private const float PixelsPerEm = 16f;

        // Noto Mono carries a full ttfautohint program set, so the bytecode hinter runs.
        private static GlyphTypeface CreateNoto()
            => SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf")).CreateGlyphTypeface();

        [Theory]
        [InlineData(FontSimulations.Bold, 4)]
        [InlineData(FontSimulations.Oblique, 4)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, 4)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique, 0)]
        public void Simulated_Variants_Hint_Like_Their_Source(FontSimulations simulations, int backwardCompatibility)
        {
            var source = CreateNoto();
            var variant = source.WithSimulations(simulations);
            var scaleQ = GlyphMaskKey.QuantizeScale(PixelsPerEm);

            var sourceHinter = source.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);
            var variantHinter = variant.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);

            Assert.NotNull(sourceHinter);
            Assert.NotNull(variantHinter);

            foreach (var c in "Hdo")
            {
                var glyph = source.CharacterToGlyphMap[c];

                Assert.True(sourceHinter!.TryHint(glyph, backwardCompatibility));

                var expected = Snapshot(sourceHinter.Zone!);

                Assert.True(variantHinter!.TryHint(glyph, backwardCompatibility));

                // The phantom points come from the side bearing; the embolden and slant
                // happen after hinting, so they must not reach the program's input.
                Assert.Equal(expected, Snapshot(variantHinter.Zone!));
            }
        }

        [Theory]
        [InlineData(FontSimulations.Bold)]
        [InlineData(FontSimulations.Oblique)]
        [InlineData(FontSimulations.Bold | FontSimulations.Oblique)]
        public void Simulated_Variants_Share_The_Source_Rasterization_State(FontSimulations simulations)
        {
            var source = CreateNoto();
            var variant = source.WithSimulations(simulations);
            var scaleQ = GlyphMaskKey.QuantizeScale(PixelsPerEm);

            Assert.Same(source.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased),
                variant.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased));
            Assert.Same(source.ProgramTables, variant.ProgramTables);
            Assert.Same(source.GridFit, variant.GridFit);
            Assert.Same(source.StemWidths, variant.StemWidths);
            Assert.Same(source.Gasp, variant.Gasp);
            Assert.Same(source.MaskCache, variant.MaskCache);

            // Slug payloads bake the simulation into the curves, so they stay per variant.
            Assert.NotSame(source.SlugCache, variant.SlugCache);
            Assert.NotSame(source.SlugStore, variant.SlugStore);
        }

        [Fact]
        public void Variants_Of_A_Face_Created_Simulated_Share_One_Unsimulated_State()
        {
            // Created with a simulation, the face has no unsimulated source of its own; its
            // variants derive from it and must still reach one shared unsimulated face.
            var simulated = SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf"))
                .CreateGlyphTypeface(FontSimulations.Bold);
            var variant = simulated.WithSimulations(FontSimulations.Oblique);
            var scaleQ = GlyphMaskKey.QuantizeScale(PixelsPerEm);

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, variant.FontSimulations);
            Assert.Same(simulated.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased),
                variant.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased));
            Assert.Same(simulated.MaskCache, variant.MaskCache);
        }

        private static string Snapshot(TrueTypeZone zone)
        {
            var builder = new System.Text.StringBuilder();

            for (var i = 0; i < zone.PointCount; i++)
            {
                builder.Append(zone.CurX[i]).Append(',').Append(zone.CurY[i]).Append(';');
            }

            return builder.ToString();
        }
    }
}
