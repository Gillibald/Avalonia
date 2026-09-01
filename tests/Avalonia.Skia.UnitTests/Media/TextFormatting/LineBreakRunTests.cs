#nullable enable

using System;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media.TextFormatting
{
    /// <summary>
    /// The characters a line ends with are carried by a <see cref="TextEndOfLine"/> run instead of
    /// being handed to the shaper, so what a line break looks like no longer depends on the font
    /// having a glyph to hide it behind.
    /// </summary>
    public class LineBreakRunTests
    {
        [Theory]
        [InlineData("abc\r\n", "abc", "\r\n")]
        [InlineData("abc\n", "abc", "\n")]
        [InlineData("abc\r", "abc", "\r")]
        [InlineData("abc\u2028", "abc", "\u2028")]
        [InlineData("abc\u0085", "abc", "\u0085")]
        public void Break_Characters_Are_Carried_By_An_End_Of_Line_Run(string text, string expectedShaped,
            string expectedBreak)
        {
            using (Start())
            {
                var textLine = FormatFirstLine(text);

                Assert.Equal(text.Length, textLine.Length);

                var shapedRun = Assert.IsType<ShapedTextRun>(textLine.TextRuns[0]);

                Assert.Equal(expectedShaped, shapedRun.Text.ToString());

                var endOfLine = Assert.IsType<TextEndOfLine>(textLine.TextRuns[1]);

                Assert.Equal(expectedBreak, endOfLine.Text.ToString());
                Assert.Equal(expectedBreak.Length, endOfLine.Length);

                // No shaped run holds a break character any more.
                Assert.DoesNotContain(textLine.TextRuns.OfType<ShapedTextRun>(),
                    run => run.Text.Span.IndexOfAny('\r', '\n') >= 0);
            }
        }

        [Fact]
        public void Break_Run_Carries_The_Properties_Of_The_Text_It_Was_Split_From()
        {
            using (Start())
            {
                var foreground = Brushes.Red.ToImmutable();
                var properties = new GenericTextRunProperties(Typeface.Default, 12, foregroundBrush: foreground);

                var textLine = FormatFirstLine("abc\r\n", properties);

                Assert.Equal(foreground, textLine.TextRuns[1].Properties?.ForegroundBrush);
            }
        }

        // The split point is the start of the break sequence, not LineBreak.PositionMeasure, which
        // also drops the run of real spaces in front of it.
        [Fact]
        public void Whitespace_Before_The_Break_Stays_In_The_Shaped_Run()
        {
            using (Start())
            {
                var textLine = FormatFirstLine("abc  \r\n");

                Assert.Equal("abc  ", Assert.IsType<ShapedTextRun>(textLine.TextRuns[0]).Text.ToString());
                Assert.Equal("\r\n", textLine.TextRuns[1].Text.ToString());

                Assert.Equal(2, textLine.NewLineLength);
                Assert.Equal(4, textLine.TrailingWhitespaceLength);
            }
        }

        [Theory]
        [InlineData("abc\r\n", 2)]
        [InlineData("abc\n", 1)]
        [InlineData("\r\nabc", 2)]
        public void NewLineLength_Comes_From_The_Break_Run(string text, int expectedNewLineLength)
        {
            using (Start())
            {
                Assert.Equal(expectedNewLineLength, FormatFirstLine(text).NewLineLength);
            }
        }

        // Text that starts with a newline produces a line whose only run is the break.
        [Fact]
        public void Line_That_Is_Only_A_Break_Still_Ends_At_A_Break()
        {
            using (Start())
            {
                var textLine = FormatFirstLine("\r\nabc");

                var endOfLine = Assert.IsType<TextEndOfLine>(Assert.Single(textLine.TextRuns));

                Assert.Equal("\r\n", endOfLine.Text.ToString());
                Assert.Equal(2, textLine.Length);
                Assert.Equal(0, textLine.Width);

                Assert.NotNull(textLine.TextLineBreak);
                Assert.Same(endOfLine, textLine.TextLineBreak!.TextEndOfLine);
            }
        }

        // The end of the visible line and the position after the break are both caret stops, and
        // stepping forwards and backwards has to agree on them.
        [Fact]
        public void Caret_Steps_Through_The_Break_Run()
        {
            using (Start())
            {
                var textLine = FormatFirstLine("ab\r\n");

                var hit = new CharacterHit(1);

                hit = textLine.GetNextCaretCharacterHit(hit);
                Assert.Equal(2, hit.FirstCharacterIndex + hit.TrailingLength);

                hit = textLine.GetNextCaretCharacterHit(hit);
                Assert.Equal(4, hit.FirstCharacterIndex + hit.TrailingLength);

                // Nothing further to move to.
                Assert.Equal(hit, textLine.GetNextCaretCharacterHit(hit));

                hit = textLine.GetPreviousCaretCharacterHit(hit);
                Assert.Equal(2, hit.FirstCharacterIndex + hit.TrailingLength);

                hit = textLine.GetPreviousCaretCharacterHit(hit);
                Assert.Equal(1, hit.FirstCharacterIndex + hit.TrailingLength);
            }
        }

        [Fact]
        public void Break_Run_Has_No_Width()
        {
            using (Start())
            {
                var withBreak = FormatFirstLine("abc\r\n");
                var withoutBreak = FormatFirstLine("abc");

                Assert.Equal(withoutBreak.WidthIncludingTrailingWhitespace,
                    withBreak.WidthIncludingTrailingWhitespace, 3);
            }
        }

        // In an RTL paragraph the break run terminates the line in logical order, so it has to stay
        // the visually first run rather than be reordered into the text as an LTR island would be.
        [Theory]
        [InlineData("hello\r\nworld")]
        [InlineData("مرحبا\r\nبالعالم")]
        public void Break_Run_Is_Visually_First_In_A_RightToLeft_Paragraph(string text)
        {
            using (Start())
            {
                var textLine = FormatFirstLine(text, flowDirection: FlowDirection.RightToLeft);

                Assert.IsType<TextEndOfLine>(textLine.TextRuns[0]);
                Assert.Equal(2, textLine.NewLineLength);
            }
        }

        private static TextLine FormatFirstLine(string text, GenericTextRunProperties? properties = null,
            FlowDirection flowDirection = FlowDirection.LeftToRight)
        {
            var defaultProperties = properties ?? new GenericTextRunProperties(Typeface.Default, 12);

            var paragraphProperties = new GenericTextParagraphProperties(flowDirection, TextAlignment.Left,
                true, true, defaultProperties, TextWrapping.NoWrap, 0, 0, 0);

            var textLine = new TextFormatterImpl().FormatLine(
                new SingleBufferTextSource(text, defaultProperties), 0, double.PositiveInfinity,
                paragraphProperties);

            Assert.NotNull(textLine);

            return textLine!;
        }

        private static IDisposable Start()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new CustomFontManagerImpl()));
    }
}
