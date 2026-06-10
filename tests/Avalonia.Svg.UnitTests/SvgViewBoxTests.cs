using System;
using Avalonia.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgViewBoxTests
{
    [Theory]
    [InlineData("0 0 100 50")]
    [InlineData("0,0,100,50")]
    [InlineData("  0 , 0\t100\n50 ")]
    public void Parses_ViewBox(string input)
    {
        Assert.True(SvgViewBox.TryParse(input.AsSpan(), out var viewBox));
        Assert.Equal(0, viewBox.X);
        Assert.Equal(0, viewBox.Y);
        Assert.Equal(100, viewBox.Width);
        Assert.Equal(50, viewBox.Height);
    }

    [Theory]
    [InlineData("0 0 -100 50")]
    [InlineData("0 0 100")]
    [InlineData("0 0 100 50 7")]
    [InlineData("")]
    public void Invalid_ViewBox_Fails(string input)
    {
        Assert.False(SvgViewBox.TryParse(input.AsSpan(), out _));
    }

    [Fact]
    public void Parses_PreserveAspectRatio()
    {
        Assert.True(SvgPreserveAspectRatio.TryParse("none".AsSpan(), out var none));
        Assert.Equal(SvgAspectRatioAlign.None, none.Align);

        Assert.True(SvgPreserveAspectRatio.TryParse("xMaxYMin slice".AsSpan(), out var slice));
        Assert.Equal(SvgAspectRatioAlign.XMaxYMin, slice.Align);
        Assert.True(slice.Slice);

        Assert.False(SvgPreserveAspectRatio.TryParse("xMidYMid sclice".AsSpan(), out _));
        Assert.False(SvgPreserveAspectRatio.TryParse("diagonal".AsSpan(), out _));
    }

    [Fact]
    public void Uniform_Scale_Centers_With_Meet()
    {
        var viewBox = new SvgViewBox(0, 0, 100, 100);
        var transform = SvgPreserveAspectRatio.Default.ComputeTransform(viewBox, new Size(200, 100));

        // meet: scale = min(2, 1) = 1, centered horizontally at +50.
        Assert.Equal(new Point(50, 0), new Point(0, 0).Transform(transform));
        Assert.Equal(new Point(150, 100), new Point(100, 100).Transform(transform));
    }

    [Fact]
    public void Uniform_Scale_Covers_With_Slice()
    {
        var viewBox = new SvgViewBox(0, 0, 100, 100);
        var par = new SvgPreserveAspectRatio(SvgAspectRatioAlign.XMidYMid, slice: true);
        var transform = par.ComputeTransform(viewBox, new Size(200, 100));

        // slice: scale = max(2, 1) = 2, centered vertically at -50.
        Assert.Equal(new Point(0, -50), new Point(0, 0).Transform(transform));
        Assert.Equal(new Point(200, 150), new Point(100, 100).Transform(transform));
    }

    [Fact]
    public void None_Scales_NonUniformly()
    {
        var viewBox = new SvgViewBox(0, 0, 100, 50);
        var par = new SvgPreserveAspectRatio(SvgAspectRatioAlign.None, slice: false);
        var transform = par.ComputeTransform(viewBox, new Size(200, 200));

        Assert.Equal(new Point(200, 200), new Point(100, 50).Transform(transform));
    }

    [Fact]
    public void ViewBox_Origin_Is_Translated_Away()
    {
        var viewBox = new SvgViewBox(10, 20, 100, 100);
        var transform = SvgPreserveAspectRatio.Default.ComputeTransform(viewBox, new Size(100, 100));

        Assert.Equal(new Point(0, 0), new Point(10, 20).Transform(transform));
    }
}
