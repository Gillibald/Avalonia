#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media.TextFormatting
{
    /// <summary>
    /// The formatter tries the face of an earlier fallback run before it asks the font manager, so
    /// consecutive runs of one script stay on one face. Only the fallback face may carry over: the
    /// current run's own style, weight and stretch stay, and a family a span requested for itself
    /// never becomes the fallback of the spans after it.
    /// </summary>
    public class FallbackReuseRunPropertiesTests
    {
        // "Noto Mono" is the primary and has no CJK. Both regional subsets cover U+4E2D; without a
        // culture the font manager answers it with the Japanese subset.
        private const string PrimaryFont = "Avalonia.Skia.UnitTests.Assets.NotoMono-Regular.ttf";
        private const string NotoSansScFont = "Avalonia.Skia.UnitTests.Fonts.NotoSansSC-Subset.ttf";
        private const string NotoSansJpFont = "Avalonia.Skia.UnitTests.Fonts.NotoSansJP-Subset.ttf";

        private const string Han = "中";

        public static IEnumerable<object[]> FirstSpanTypefaceProperties => new[]
        {
            new object[] { FontStyle.Normal, FontWeight.Bold, FontStretch.Normal },
            new object[] { FontStyle.Italic, FontWeight.Normal, FontStretch.Normal },
            new object[] { FontStyle.Normal, FontWeight.Normal, FontStretch.Condensed },
        };

        [Theory]
        [MemberData(nameof(FirstSpanTypefaceProperties))]
        public void A_Fallback_Run_Should_Keep_Its_Own_Style_Weight_And_Stretch_When_It_Reuses_The_Previous_Face(
            FontStyle firstStyle, FontWeight firstWeight, FontStretch firstStretch)
        {
            using (Start(out _))
            {
                var first = Properties(new Typeface(FontFamily.Default, firstStyle, firstWeight, firstStretch));
                var second = Properties(Typeface.Default);

                var runs = FormatRuns((Han, first), (Han, second));

                Assert.Equal(2, runs.Count);
                Assert.Equal("Noto Sans JP", runs[0].Properties.CachedGlyphTypeface.FamilyName);
                Assert.Equal("Noto Sans JP", runs[1].Properties.CachedGlyphTypeface.FamilyName);

                var secondTypeface = runs[1].Properties.Typeface;

                Assert.Equal(FontStyle.Normal, secondTypeface.Style);
                Assert.Equal(FontWeight.Normal, secondTypeface.Weight);
                Assert.Equal(FontStretch.Normal, secondTypeface.Stretch);
            }
        }

        [Fact]
        public void A_Span_Should_Not_Take_A_Family_The_Previous_Span_Requested_For_Itself()
        {
            using (Start(out var fonts))
            {
                var requested = Properties(new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans SC")));
                var plain = Properties(Typeface.Default);

                // The first span names the Simplified Chinese face and gets it as its primary. The
                // second span names nothing, so its ideograph belongs to the font manager, which
                // answers it with the Japanese face.
                var runs = FormatRuns((Han, requested), (Han, plain));

                Assert.Equal(2, runs.Count);
                Assert.Equal(requested.Typeface, runs[0].Properties.Typeface);
                Assert.Equal("Noto Sans JP", runs[1].Properties.CachedGlyphTypeface.FamilyName);
                Assert.Contains(0x4E2D, fonts.MatchedCodepoints);
            }
        }

        [Fact]
        public void Consecutive_Fallback_Runs_Of_One_Script_Should_Ask_The_Font_Manager_Once()
        {
            using (Start(out var fonts))
            {
                var first = Properties(new Typeface(FontFamily.Default, weight: FontWeight.Bold));
                var second = Properties(Typeface.Default);
                var third = Properties(Typeface.Default);

                var runs = FormatRuns((Han, first), (Han, second), (Han, third));

                Assert.Equal(3, runs.Count);
                Assert.All(runs, run => Assert.Equal("Noto Sans JP", run.Properties.CachedGlyphTypeface.FamilyName));
                Assert.Equal(new[] { 0x4E2D }, fonts.MatchedCodepoints);
            }
        }

        [Fact]
        public void Latin_After_A_Cjk_Fallback_Run_Should_Return_To_The_Primary()
        {
            using (Start(out _))
            {
                var first = Properties(Typeface.Default);
                var second = Properties(Typeface.Default);

                var runs = FormatRuns((Han, first), ("ab", second));

                Assert.Equal(2, runs.Count);
                Assert.Equal("Noto Sans JP", runs[0].Properties.CachedGlyphTypeface.FamilyName);
                Assert.Equal(Typeface.Default, runs[1].Properties.Typeface);
            }
        }

        private static GenericTextRunProperties Properties(Typeface typeface)
            => new GenericTextRunProperties(typeface, foregroundBrush: Brushes.Black);

        private static List<ShapedTextRun> FormatRuns(params (string Text, TextRunProperties Properties)[] spans)
        {
            var source = new TextFormatterTests.ListTextSource(
                spans.Select(span => (TextRun)new TextCharacters(span.Text, span.Properties)));

            var textLine = TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity,
                new GenericTextParagraphProperties(spans[0].Properties));

            Assert.NotNull(textLine);

            return textLine.TextRuns.OfType<ShapedTextRun>().ToList();
        }

        private static IDisposable Start(out CountingFontCollection fonts)
        {
            var disposable = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface()));

            // The curated set becomes the system fonts; the default family resolves against it.
            AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(
                new FontManagerOptions { DefaultFamilyName = "fonts:SystemFonts#Noto Mono" });

            fonts = new CountingFontCollection(
                new CuratedSystemFontCollection(PrimaryFont, NotoSansScFont, NotoSansJpFont));

            FontManager.Current.AddFontCollection(fonts);

            return disposable;
        }

        private sealed class CuratedSystemFontCollection : FontCollectionBase
        {
            public CuratedSystemFontCollection(params string[] fontResourceNames)
            {
                foreach (var name in fontResourceNames)
                {
                    TryAddFontSource(new Uri($"resm:{name}?assembly=Avalonia.Skia.UnitTests"));
                }
            }

            public override Uri Key => FontManager.SystemFontsKey;
        }

        /// <summary>
        /// The system collection, recording every codepoint the font manager asks it to match.
        /// </summary>
        private sealed class CountingFontCollection : IFontCollection
        {
            private readonly IFontCollection _inner;

            public CountingFontCollection(IFontCollection inner)
            {
                _inner = inner;
            }

            public List<int> MatchedCodepoints { get; } = new();

            public Uri Key => _inner.Key;

            public int Count => _inner.Count;

            public FontFamily this[int index] => _inner[index];

            public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight,
                FontStretch fontStretch, string? familyName, CultureInfo? culture, out Typeface typeface)
            {
                MatchedCodepoints.Add(codepoint);

                return _inner.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, familyName,
                    culture, out typeface);
            }

            public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
                => _inner.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);

            public bool TryGetFamilyTypefaces(string familyName,
                [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
                => _inner.TryGetFamilyTypefaces(familyName, out familyTypefaces);

            public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style,
                FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
                => _inner.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch,
                    out syntheticGlyphTypeface);

            public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
                => _inner.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);

            public IEnumerator<FontFamily> GetEnumerator() => _inner.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Dispose() => _inner.Dispose();
        }
    }
}
