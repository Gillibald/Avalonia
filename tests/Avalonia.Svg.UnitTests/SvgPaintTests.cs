using Avalonia.Media;
using Avalonia.Media.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgPaintTests
{
    [Fact]
    public void Parses_None()
    {
        Assert.True(SvgPaint.TryParse("none", out var paint));
        Assert.Equal(SvgPaintKind.None, paint.Kind);
    }

    [Fact]
    public void Parses_CurrentColor()
    {
        Assert.True(SvgPaint.TryParse("currentColor", out var paint));
        Assert.Equal(SvgPaintKind.CurrentColor, paint.Kind);
    }

    [Theory]
    [InlineData("#ff0000")]
    [InlineData("#f00")]
    [InlineData("red")]
    [InlineData("rgb(255,0,0)")]
    public void Parses_Colors(string input)
    {
        Assert.True(SvgPaint.TryParse(input, out var paint), $"'{input}' should parse");
        Assert.Equal(SvgPaintKind.Color, paint.Kind);
        Assert.Equal(Colors.Red, paint.Color);
    }

    [Fact]
    public void Parses_Url_Reference()
    {
        Assert.True(SvgPaint.TryParse("url(#gradient1)", out var paint));
        Assert.Equal(SvgPaintKind.Reference, paint.Kind);
        Assert.Equal("gradient1", paint.Reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notacolor")]
    [InlineData("url(grad)")]
    public void Invalid_Input_Fails(string input)
    {
        Assert.False(SvgPaint.TryParse(input, out _));
    }
}
