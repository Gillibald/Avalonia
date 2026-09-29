using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Moq;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    public class GlyphTypefaceSimulationTests
    {
        private const string InterRegularAsset =
            "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private const string InterVariableAsset =
            "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";

        private static readonly OpenTypeTag s_wghtTag = OpenTypeTag.Parse("wght");

        private static GlyphTypeface LoadTypeface(string assetUri)
        {
            var assetLoader = new StandardAssetLoader();
            using var stream = assetLoader.Open(new Uri(assetUri));
            return new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));
        }

        private static NormalizedVariationPosition WghtPosition(GlyphTypeface gt, double weight)
            => gt.CreateNormalizedPosition(new FontVariationSettings(
                new[] { new FontVariation(s_wghtTag, weight) }));

        [Fact]
        public void WithSimulations_Returns_Self_For_Own_Simulations()
        {
            var gt = LoadTypeface(InterRegularAsset);

            Assert.Same(gt, gt.WithSimulations(FontSimulations.None));

            var bold = gt.WithSimulations(FontSimulations.Bold);

            Assert.Same(bold, bold.WithSimulations(FontSimulations.Bold));
        }

        [Fact]
        public void WithSimulations_Returns_Same_Instance_For_Same_Simulations()
        {
            var gt = LoadTypeface(InterRegularAsset);

            var first = gt.WithSimulations(FontSimulations.Bold);
            var second = gt.WithSimulations(FontSimulations.Bold);

            Assert.NotSame(gt, first);
            Assert.Same(first, second);
            Assert.Equal(FontSimulations.Bold, first.FontSimulations);
        }

        [Fact]
        public void WithSimulations_On_Variant_Resolves_From_The_Unsimulated_Face()
        {
            var gt = LoadTypeface(InterRegularAsset);

            var bold = gt.WithSimulations(FontSimulations.Bold);
            var boldOblique = bold.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique);

            Assert.Same(gt.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique), boldOblique);
            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, boldOblique.FontSimulations);

            // Simulations do not stack: asking a variant for fewer simulations removes them.
            Assert.Same(gt.WithSimulations(FontSimulations.Oblique), boldOblique.WithSimulations(FontSimulations.Oblique));
            Assert.Same(gt, boldOblique.WithSimulations(FontSimulations.None));
            Assert.Same(gt, boldOblique.RootTypeface);
        }

        [Fact]
        public void WithSimulations_Keeps_Simulations_Baked_In_At_Construction()
        {
            var assetLoader = new StandardAssetLoader();
            using var stream = assetLoader.Open(new Uri(InterRegularAsset));
            var gt = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream), FontSimulations.Bold);

            Assert.Same(gt, gt.WithSimulations(FontSimulations.None));

            var boldOblique = gt.WithSimulations(FontSimulations.Oblique);

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, boldOblique.FontSimulations);
            Assert.Same(boldOblique, gt.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique));
        }

        [Fact]
        public void Variant_Reports_Simulated_Weight_And_Style()
        {
            var gt = LoadTypeface(InterRegularAsset);

            var bold = gt.WithSimulations(FontSimulations.Bold);
            var oblique = gt.WithSimulations(FontSimulations.Oblique);
            var boldOblique = gt.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique);

            Assert.Equal(FontWeight.Bold, bold.Weight);
            Assert.Equal(FontStyle.Normal, bold.Style);

            Assert.Equal(gt.Weight, oblique.Weight);
            Assert.Equal(FontStyle.Italic, oblique.Style);

            Assert.Equal(FontWeight.Bold, boldOblique.Weight);
            Assert.Equal(FontStyle.Italic, boldOblique.Style);

            Assert.Equal(gt.Stretch, boldOblique.Stretch);
        }

        [Fact]
        public void Variant_Shares_Font_Data_And_Metrics_With_Its_Source()
        {
            var gt = LoadTypeface(InterRegularAsset);

            var variant = gt.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique);

            Assert.Same(gt.FontMemory, variant.FontMemory);
            Assert.Equal(gt.Metrics, variant.Metrics);
            Assert.Same(gt.FamilyNames, variant.FamilyNames);
            Assert.Equal(gt.FamilyName, variant.FamilyName);
            Assert.Equal(gt.GlyphCount, variant.GlyphCount);

            Assert.True(gt.CharacterToGlyphMap.TryGetGlyph('A', out var glyph));
            Assert.True(gt.TryGetHorizontalGlyphAdvance(glyph, out var advance));
            Assert.True(variant.TryGetHorizontalGlyphAdvance(glyph, out var variantAdvance));
            Assert.Equal(advance, variantAdvance);
        }

        [Fact]
        public void Variant_Shares_The_Parsed_STAT_Table_With_Its_Root()
        {
            var gt = LoadTypeface(InterVariableAsset);
            var clone = gt.WithVariation(WghtPosition(gt, 700));

            Assert.NotNull(gt.StatTable);
            Assert.Same(gt.StatTable, gt.WithSimulations(FontSimulations.Bold).StatTable);
            Assert.Same(gt.StatTable, clone.WithSimulations(FontSimulations.Oblique).StatTable);
        }

        [Fact]
        public void Variant_Shares_Render_And_Shaper_Typefaces_With_Its_Source()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                var gt = LoadTypeface(InterRegularAsset);

                var variant = gt.WithSimulations(FontSimulations.Bold);

                // The variant materializes first, so sharing cannot depend on the source's order.
                Assert.Same(variant.PlatformTypeface, gt.PlatformTypeface);
                Assert.Same(variant.TextShaperTypeface, gt.TextShaperTypeface);
            }
        }

        [Fact]
        public void WithVariation_On_Variant_Keeps_Simulations()
        {
            var gt = LoadTypeface(InterVariableAsset);
            var position = WghtPosition(gt, 300);

            var bold = gt.WithSimulations(FontSimulations.Bold);
            var variedBold = bold.WithVariation(position);

            Assert.Equal(FontSimulations.Bold, variedBold.FontSimulations);
            Assert.Equal(position, variedBold.VariationPosition);
            Assert.Equal(FontWeight.Bold, variedBold.Weight);

            // The variant hangs off the variation clone, which the source's cache owns.
            Assert.Same(gt.WithVariation(position).WithSimulations(FontSimulations.Bold), variedBold);
            Assert.Same(bold, variedBold.WithVariation(default));
            Assert.Same(gt, variedBold.RootTypeface);
        }

        [Fact]
        public void WithSimulations_On_Variation_Clone_Keeps_Position()
        {
            var gt = LoadTypeface(InterVariableAsset);
            var position = WghtPosition(gt, 700);

            var varied = gt.WithVariation(position);
            var oblique = varied.WithSimulations(FontSimulations.Oblique);

            Assert.Equal(position, oblique.VariationPosition);
            Assert.Equal(varied.Weight, oblique.Weight);
            Assert.Equal(varied.Metrics, oblique.Metrics);
            Assert.Same(varied, oblique.WithSimulations(FontSimulations.None));
            Assert.Same(gt, oblique.RootTypeface);
        }

        [Fact]
        public void Disposing_The_Root_Disposes_Each_Render_Typeface_Once()
        {
            var created = new List<CountingTypeface>();
            var renderInterface = new Mock<IPlatformRenderInterface>();

            renderInterface
                .Setup(x => x.CreateTypeface(It.IsAny<GlyphTypeface>()))
                .Returns(() =>
                {
                    var typeface = new CountingTypeface();
                    created.Add(typeface);
                    return typeface;
                });

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: renderInterface.Object)))
            {
                var gt = LoadTypeface(InterVariableAsset);
                var varied = gt.WithVariation(WghtPosition(gt, 700));

                var faces = new[]
                {
                    gt,
                    gt.WithSimulations(FontSimulations.Bold),
                    gt.WithSimulations(FontSimulations.Bold | FontSimulations.Oblique),
                    varied,
                    varied.WithSimulations(FontSimulations.Oblique)
                };

                foreach (var face in faces)
                {
                    _ = face.PlatformTypeface;
                }

                // One render typeface for the default instance and one for the clone.
                Assert.Equal(2, created.Count);

                // A variant releases nothing it shares.
                faces[1].Dispose();
                faces[4].Dispose();

                Assert.All(created, t => Assert.Equal(0, t.DisposeCount));
                Assert.Same(created[0], gt.PlatformTypeface);

                gt.Dispose();

                Assert.All(created, t => Assert.Equal(1, t.DisposeCount));
            }
        }

        private sealed class CountingTypeface : IPlatformTypeface
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;
        }
    }
}
