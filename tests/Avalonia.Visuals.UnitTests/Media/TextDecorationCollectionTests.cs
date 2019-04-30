using System;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Visuals.UnitTests.Media
{
    public class TextDecorationCollectionTests
    {
        [Fact]
        public void Should_Parse_TextDecorations()
        {
            var decorations = TextDecorationCollection.Parse(nameof(TextDecorationLocation.Baseline) + "," +
                                                             nameof(TextDecorationLocation.Overline) + "," +
                                                             nameof(TextDecorationLocation.Strikethrough) + "," +
                                                             nameof(TextDecorationLocation.Underline));
            Assert.Equal(4, decorations.Count);

            Assert.Equal(TextDecorationLocation.Baseline, decorations[0].Location);

            Assert.Equal(TextDecorationLocation.Overline, decorations[1].Location);

            Assert.Equal(TextDecorationLocation.Strikethrough, decorations[2].Location);

            Assert.Equal(TextDecorationLocation.Underline, decorations[3].Location);
        }

        [Fact]
        public void Should_Throw_Invalid_Operation_Exception_When_Location_Is_Parsed_Multiple_Times()
        {
            Assert.Throws<ArgumentException>(() =>
                {
                    TextDecorationCollection.Parse(nameof(TextDecorationLocation.Baseline) + "," +
                                                   nameof(TextDecorationLocation.Baseline));
                });
        }
    }
}
