using System;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.DirectWrite.UnitTests
{
    public class DirectWriteVariableFontTests
    {
        private const string SegoeUIVariableText = "Segoe UI Variable Text";

        private static readonly OpenTypeTag s_wght = OpenTypeTag.Parse("wght");
        private static readonly OpenTypeTag s_opsz = OpenTypeTag.Parse("opsz");

        [Win32Fact]
        public void Named_Instance_Reports_Its_Axis_Values()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.SkipUnless(provider.TryMatchFamily(SegoeUIVariableText, FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold), "Segoe UI Variable is not installed.");

            Assert.NotNull(bold.AxisValues);
            Assert.Equal(700, bold.AxisValues[s_wght]);
            Assert.Equal(10.5f, bold.AxisValues[s_opsz]);

            Assert.True(provider.TryMatchFamily("Arial", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal,
                out var staticFace));
            Assert.Null(staticFace.AxisValues);
        }

        [Win32Fact]
        public void Character_Match_Returns_The_Designed_Face_Instead_Of_A_Simulation()
        {
            using var provider = new DirectWriteFontProvider();

            // Mongolian Baiti, the fallback for Mongolian, has a regular face only, so DirectWrite
            // answers a bold request with a simulated bold font.
            Assert.SkipUnless(provider.TryMatchFamily("Mongolian Baiti", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _), "Mongolian Baiti is not installed.");

            Assert.True(provider.TryMatchCharacter(0x1820, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal,
                null, null, out var match));

            Assert.Equal("Mongolian Baiti", match.FamilyName);
            Assert.Equal(FontWeight.Normal, match.Weight);
        }

        [Win32Fact]
        public void Family_Match_Returns_The_Designed_Face_For_A_Style_The_Family_Lacks()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.SkipUnless(provider.TryMatchFamily("Mongolian Baiti", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _), "Mongolian Baiti is not installed.");

            // DirectWrite lists only the simulated bold font as matching; the family is still known.
            Assert.True(provider.TryMatchFamily("Mongolian Baiti", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var match));

            Assert.Equal(FontWeight.Normal, match.Weight);
        }

        [Win32Fact]
        public void System_Collection_Loads_The_Named_Instance_DirectWrite_Picks()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant(new HarfBuzzTextShaper());

            var collection = new SystemFontCollection(FontManager.SystemFontsKey, new DirectWriteFontProvider());

            try
            {
                Assert.SkipUnless(collection.TryGetGlyphTypeface(SegoeUIVariableText, FontStyle.Normal,
                    FontWeight.Bold, FontStretch.Normal, out var bold) &&
                    bold.FamilyName.StartsWith("Segoe UI Variable", StringComparison.Ordinal),
                    "Segoe UI Variable is not installed.");

                Assert.Equal(FontSimulations.None, bold.FontSimulations);
                Assert.True(bold.TryGetUserAxisValue(bold.VariationPosition, s_wght, out var wght));
                Assert.True(bold.TryGetUserAxisValue(bold.VariationPosition, s_opsz, out var opsz));
                Assert.Equal(700, wght, 0.05);
                Assert.Equal(10.5, opsz, 0.05);

                // Shaping runs on a HarfBuzz sub-font at the instance's coordinates, so without
                // kerning every shaped advance is the instance's own advance.
                const double emSize = 16;

                var buffer = TextShaper.Current.ShapeText("Hamburg", new TextShaperOptions(bold, emSize,
                    fontFeatures: new[] { FontFeature.Parse("-kern") }));

                for (var i = 0; i < buffer.Length; i++)
                {
                    Assert.True(bold.TryGetHorizontalGlyphAdvance(buffer[i].GlyphIndex, out var advance));
                    Assert.Equal(advance * emSize / bold.Metrics.DesignEmHeight, buffer[i].GlyphAdvance, 0.01);
                }

                // Every optical size family of the file keeps its own faces, all of one root.
                foreach (var (familyName, expectedOpsz) in new[] { ("Segoe UI Variable Display", 36.0), ("Segoe UI Variable Small", 8.0) })
                {
                    Assert.True(collection.TryGetGlyphTypeface(familyName, FontStyle.Normal, FontWeight.Bold,
                        FontStretch.Normal, out var face));

                    Assert.Same(bold.WithVariation(default), face.WithVariation(default));
                    Assert.Equal(FontSimulations.None, face.FontSimulations);
                    Assert.True(face.TryGetUserAxisValue(face.VariationPosition, s_opsz, out var faceOpsz));
                    Assert.True(face.TryGetUserAxisValue(face.VariationPosition, s_wght, out var faceWght));
                    Assert.Equal(expectedOpsz, faceOpsz, 0.05);
                    Assert.Equal(700, faceWght, 0.05);
                }
            }
            finally
            {
                ((IDisposable)collection).Dispose();
            }
        }
    }
}
