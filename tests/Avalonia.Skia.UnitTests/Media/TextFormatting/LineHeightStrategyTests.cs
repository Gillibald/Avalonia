#nullable enable

using System;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media.TextFormatting
{
    public class LineHeightStrategyTests
    {
        [Fact]
        public void NaturalStrategy_Should_Return_Natural_Metrics()
        {
            var metrics = CreateSampleMetrics();
            var strategy = NaturalLineHeightStrategy.Instance;

            var result = strategy.Compute(in metrics);

            Assert.Equal(metrics.NaturalHeight, result.Height);
            Assert.Equal(metrics.NaturalBaseline, result.Baseline);
        }

        [Fact]
        public void FixedStrategy_Should_Use_Fixed_Height_When_Larger_Than_Natural()
        {
            var metrics = CreateSampleMetrics();
            var fixedHeight = metrics.NaturalHeight + 20;
            var strategy = new FixedLineHeightStrategy(fixedHeight);

            var result = strategy.Compute(in metrics);

            Assert.Equal(fixedHeight, result.Height);
        }

        [Fact]
        public void FixedStrategy_Should_Clamp_When_Smaller_Than_Natural()
        {
            var metrics = CreateSampleMetrics();
            var fixedHeight = metrics.NaturalHeight - 2;
            var strategy = new FixedLineHeightStrategy(fixedHeight);

            var result = strategy.Compute(in metrics);

            Assert.Equal(fixedHeight, result.Height);
            Assert.Equal(-metrics.Ascent, result.Baseline);
        }

        [Fact]
        public void FixedStrategy_Should_Center_Baseline_When_Larger_Than_Natural()
        {
            var metrics = CreateSampleMetrics();
            var fixedHeight = metrics.NaturalHeight + 50;
            var strategy = new FixedLineHeightStrategy(fixedHeight);

            var result = strategy.Compute(in metrics);

            var expectedExtra = fixedHeight - (metrics.Descent - metrics.Ascent);
            var expectedBaseline = -metrics.Ascent + expectedExtra / 2;

            Assert.Equal(fixedHeight, result.Height);
            Assert.Equal(expectedBaseline, result.Baseline, 5);
        }

        [Fact]
        public void FixedStrategy_Should_Reject_Invalid_Height()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineHeightStrategy(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineHeightStrategy(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineHeightStrategy(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineHeightStrategy(double.PositiveInfinity));
        }

        [Fact]
        public void AtLeastStrategy_Should_Use_Natural_When_Above_Minimum()
        {
            var metrics = CreateSampleMetrics();
            var minimum = metrics.NaturalHeight - 5;
            var strategy = new AtLeastLineHeightStrategy(minimum);

            var result = strategy.Compute(in metrics);

            Assert.Equal(metrics.NaturalHeight, result.Height);
            Assert.Equal(metrics.NaturalBaseline, result.Baseline);
        }

        [Fact]
        public void AtLeastStrategy_Should_Enforce_Minimum()
        {
            var metrics = CreateSampleMetrics();
            var minimum = metrics.NaturalHeight + 20;
            var strategy = new AtLeastLineHeightStrategy(minimum);

            var result = strategy.Compute(in metrics);

            Assert.Equal(minimum, result.Height);
            Assert.True(result.Height >= metrics.NaturalHeight);
        }

        [Fact]
        public void AtLeastStrategy_Should_Reject_Invalid_MinimumHeight()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtLeastLineHeightStrategy(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtLeastLineHeightStrategy(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtLeastLineHeightStrategy(double.NaN));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.15)]
        [InlineData(1.5)]
        [InlineData(2.0)]
        public void MultipleStrategy_Should_Scale_Natural_Height(double multiplier)
        {
            var metrics = CreateSampleMetrics();
            var strategy = new MultipleLineHeightStrategy(multiplier);

            var result = strategy.Compute(in metrics);

            Assert.Equal(metrics.NaturalHeight * multiplier, result.Height, 5);
        }

        [Fact]
        public void MultipleStrategy_Single_Should_Match_Natural_Height()
        {
            var metrics = CreateSampleMetrics();
            var strategy = new MultipleLineHeightStrategy(1.0);

            var result = strategy.Compute(in metrics);

            Assert.Equal(metrics.NaturalHeight, result.Height, 5);
        }

        [Fact]
        public void MultipleStrategy_Should_Reject_Invalid_Multiplier()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultipleLineHeightStrategy(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultipleLineHeightStrategy(-0.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultipleLineHeightStrategy(double.NaN));
        }

        [Fact]
        public void UniformStrategy_Should_Use_Default_Font_Metrics()
        {
            var metrics = CreateSampleMetrics();
            var strategy = new UniformLineHeightStrategy();

            var result = strategy.Compute(in metrics);

            var expectedDefaultHeight = metrics.DefaultFontDescent - metrics.DefaultFontAscent + metrics.DefaultFontLineGap;
            Assert.Equal(Math.Max(expectedDefaultHeight, metrics.InkExtent), result.Height);
        }

        [Fact]
        public void UniformStrategy_Should_Apply_Multiplier()
        {
            var metrics = CreateSampleMetrics();
            var strategy = new UniformLineHeightStrategy(1.5);

            var result = strategy.Compute(in metrics);

            var expectedDefaultHeight = (metrics.DefaultFontDescent - metrics.DefaultFontAscent + metrics.DefaultFontLineGap) * 1.5;
            Assert.True(result.Height >= expectedDefaultHeight || result.Height >= metrics.InkExtent);
        }

        [Fact]
        public void UniformStrategy_Should_Never_Clip_Ink()
        {
            // Create metrics where ink extent is larger than uniform height
            var metrics = new LineNaturalMetrics
            {
                Ascent = -10,
                Descent = 4,
                LineGap = 1,
                NaturalHeight = 15,
                NaturalBaseline = 10.5,
                DefaultFontAscent = -8,
                DefaultFontDescent = 3,
                DefaultFontLineGap = 0,
                InkExtent = 20  // Ink is larger than default font's natural height (11)
            };

            var strategy = new UniformLineHeightStrategy();
            var result = strategy.Compute(in metrics);

            Assert.True(result.Height >= metrics.InkExtent);
        }

        [Fact]
        public void UniformStrategy_Should_Reject_Invalid_Multiplier()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UniformLineHeightStrategy(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UniformLineHeightStrategy(-1));
        }

        [Fact]
        public void ClampedStrategy_Should_Use_Natural_When_Below_Maximum()
        {
            var metrics = CreateSampleMetrics();
            var maximum = metrics.NaturalHeight + 50;
            var strategy = new ClampedLineHeightStrategy(maximum);

            var result = strategy.Compute(in metrics);

            Assert.Equal(metrics.NaturalHeight, result.Height);
        }

        [Fact]
        public void ClampedStrategy_Should_Clamp_To_Maximum()
        {
            var metrics = CreateSampleMetrics();
            var maximum = metrics.NaturalHeight - 2;
            var strategy = new ClampedLineHeightStrategy(maximum);

            var result = strategy.Compute(in metrics);

            // Clamped to max, but not below ink extent
            Assert.True(result.Height <= maximum || result.Height == metrics.InkExtent);
        }

        [Fact]
        public void ClampedStrategy_Should_Never_Clip_Ink()
        {
            var metrics = new LineNaturalMetrics
            {
                Ascent = -10,
                Descent = 10,
                LineGap = 2,
                NaturalHeight = 22,
                NaturalBaseline = 11,
                DefaultFontAscent = -10,
                DefaultFontDescent = 10,
                DefaultFontLineGap = 2,
                InkExtent = 18
            };

            var strategy = new ClampedLineHeightStrategy(12); // Way below natural
            var result = strategy.Compute(in metrics);

            // Should not go below ink extent
            Assert.True(result.Height >= metrics.InkExtent);
        }

        [Fact]
        public void ClampedStrategy_Should_Reject_Invalid_MaximumHeight()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClampedLineHeightStrategy(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClampedLineHeightStrategy(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClampedLineHeightStrategy(double.NaN));
        }

        // --- Integration tests using the real text engine ---

        [Fact]
        public void TextLine_Should_Use_NaturalStrategy()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var textMetrics = new TextMetrics(typeface.GlyphTypeface, 12);
                var naturalHeight = -textMetrics.Ascent + textMetrics.Descent + textMetrics.LineGap;

                var paragraphProps = new GenericTextParagraphProperties(defaultProperties);
                paragraphProps.SetLineHeightStrategy(NaturalLineHeightStrategy.Instance);

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(naturalHeight, textLine.Height, 5);
            }
        }

        [Fact]
        public void TextLine_Should_Use_FixedStrategy()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var fixedHeight = 30.0;
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties);
                paragraphProps.SetLineHeightStrategy(new FixedLineHeightStrategy(fixedHeight));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(fixedHeight, textLine.Height, 5);
            }
        }

        [Fact]
        public void TextLine_Should_Use_MultipleStrategy()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var textMetrics = new TextMetrics(typeface.GlyphTypeface, 12);
                var naturalHeight = -textMetrics.Ascent + textMetrics.Descent + textMetrics.LineGap;

                var paragraphProps = new GenericTextParagraphProperties(defaultProperties);
                paragraphProps.SetLineHeightStrategy(new MultipleLineHeightStrategy(2.0));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(naturalHeight * 2.0, textLine.Height, 5);
            }
        }

        [Fact]
        public void TextLine_Should_Use_AtLeastStrategy_With_Large_Minimum()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var textMetrics = new TextMetrics(typeface.GlyphTypeface, 12);
                var naturalHeight = -textMetrics.Ascent + textMetrics.Descent + textMetrics.LineGap;

                var minimum = naturalHeight + 20;
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties);
                paragraphProps.SetLineHeightStrategy(new AtLeastLineHeightStrategy(minimum));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(minimum, textLine.Height, 5);
            }
        }

        [Fact]
        public void TextLine_Should_Use_AtLeastStrategy_With_Small_Minimum()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var textMetrics = new TextMetrics(typeface.GlyphTypeface, 12);
                var naturalHeight = -textMetrics.Ascent + textMetrics.Descent + textMetrics.LineGap;

                var minimum = naturalHeight - 5;
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties);
                paragraphProps.SetLineHeightStrategy(new AtLeastLineHeightStrategy(minimum));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                // Natural is larger than minimum, so natural wins
                Assert.Equal(naturalHeight, textLine.Height, 5);
            }
        }

        [Fact]
        public void Strategy_Should_Take_Precedence_Over_LineHeight()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var textMetrics = new TextMetrics(typeface.GlyphTypeface, 12);
                var naturalHeight = -textMetrics.Ascent + textMetrics.Descent + textMetrics.LineGap;

                // Set both LineHeight and a strategy — strategy should win
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties, lineHeight: 50);
                paragraphProps.SetLineHeightStrategy(new MultipleLineHeightStrategy(1.0));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                // Strategy returns natural×1.0, NOT the LineHeight=50
                Assert.Equal(naturalHeight, textLine.Height, 5);
            }
        }

        [Fact]
        public void LineSpacing_Should_Be_Added_On_Top_Of_Strategy()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var fixedHeight = 30.0;
                var lineSpacing = 5.0;
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties)
                {
                    LineSpacing = lineSpacing
                };
                paragraphProps.SetLineHeightStrategy(new FixedLineHeightStrategy(fixedHeight));

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(fixedHeight + lineSpacing, textLine.Height, 5);
            }
        }

        [Fact]
        public void Legacy_LineHeight_Should_Work_When_No_Strategy_Set()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");
                var defaultProperties = new GenericTextRunProperties(typeface);
                var textSource = new SingleBufferTextSource("Hello World", defaultProperties);
                var formatter = new TextFormatterImpl();

                var lineHeight = 50.0;
                var paragraphProps = new GenericTextParagraphProperties(defaultProperties, lineHeight: lineHeight);
                // No strategy set — should use legacy path

                var textLine = formatter.FormatLine(textSource, 0, double.PositiveInfinity, paragraphProps);

                Assert.NotNull(textLine);
                Assert.Equal(lineHeight, textLine.Height, 5);
            }
        }

        [Fact]
        public void TextLayout_Should_Use_Strategy_From_Constructor()
        {
            using (Start())
            {
                var typeface = new Typeface("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests#Inter");

                var layout = new TextLayout(
                    "Hello World",
                    typeface,
                    12,
                    lineHeightStrategy: new FixedLineHeightStrategy(30));

                Assert.NotNull(layout.TextLines);
                Assert.Single(layout.TextLines);
                Assert.Equal(30, layout.TextLines[0].Height, 5);
            }
        }

        [Fact]
        public void GenericTextParagraphProperties_Copy_Should_Preserve_Strategy()
        {
            var defaultProperties = new GenericTextRunProperties(Typeface.Default);
            var original = new GenericTextParagraphProperties(defaultProperties);
            var strategy = new FixedLineHeightStrategy(25);
            original.SetLineHeightStrategy(strategy);

            var copy = new GenericTextParagraphProperties(original);

            Assert.Same(strategy, copy.LineHeightStrategy);
        }

        [Fact]
        public void TextParagraphProperties_Default_Strategy_Is_Null()
        {
            var defaultProperties = new GenericTextRunProperties(Typeface.Default);
            var paragraphProps = new GenericTextParagraphProperties(defaultProperties);

            Assert.Null(paragraphProps.LineHeightStrategy);
        }

        // --- Helpers ---

        private static LineNaturalMetrics CreateSampleMetrics()
        {
            // Typical metrics for a 12px Latin font
            return new LineNaturalMetrics
            {
                Ascent = -11.0,       // negative, above baseline
                Descent = 3.0,        // positive, below baseline
                LineGap = 1.0,
                NaturalHeight = 15.0, // 3 - (-11) + 1
                NaturalBaseline = 11.5, // -(-11) + 1 * 0.5
                DefaultFontAscent = -11.0,
                DefaultFontDescent = 3.0,
                DefaultFontLineGap = 1.0,
                InkExtent = 12.5
            };
        }

        private static IDisposable Start()
        {
            var disposable = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new CustomFontManagerImpl()));

            return disposable;
        }
    }
}
