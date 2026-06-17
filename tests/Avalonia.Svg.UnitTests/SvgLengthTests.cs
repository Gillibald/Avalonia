using System;
using Avalonia.Media.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgLengthTests
{
    private static double Resolve(string input, SvgLengthAxis axis = SvgLengthAxis.Other, double width = 0, double height = 0)
    {
        Assert.True(SvgLength.TryParse(input.AsSpan(), out var length), $"'{input}' should parse");
        return length.Resolve(axis, new Size(width, height));
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("10px", 10)]
    [InlineData("-2.5", -2.5)]
    [InlineData("72pt", 96)]
    [InlineData("6pc", 96)]
    [InlineData("1in", 96)]
    [InlineData("25.4mm", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("101.6Q", 96)]
    [InlineData("101.6q", 96)]
    [InlineData("2em", 32)]
    [InlineData("2ex", 16)]
    [InlineData("1e1", 10)]
    public void Resolves_Absolute_Units(string input, double expected)
    {
        Assert.Equal(expected, Resolve(input), 9);
    }

    [Fact]
    public void Units_Match_Case_Insensitively()
    {
        // SVG 2 geometry attributes take CSS lengths; CSS units are
        // ASCII case-insensitive.
        Assert.Equal(10, Resolve("10PX"), 9);
        Assert.Equal(96, Resolve("25.4Mm"), 9);
    }

    [Fact]
    public void Font_Relative_Units_Resolve_Against_The_Given_Font_Sizes()
    {
        Assert.True(SvgLength.TryParse("2rem".AsSpan(), out var rem));
        Assert.Equal(64, rem.Resolve(SvgLengthAxis.Other, default, fontSize: 20, rootFontSize: 32), 9);

        Assert.True(SvgLength.TryParse("2em".AsSpan(), out var em));
        Assert.Equal(40, em.Resolve(SvgLengthAxis.Other, default, fontSize: 20, rootFontSize: 32), 9);

        // ch uses the spec-sanctioned 0.5em fallback (no glyph metrics here).
        Assert.True(SvgLength.TryParse("2ch".AsSpan(), out var ch));
        Assert.Equal(20, ch.Resolve(SvgLengthAxis.Other, default, fontSize: 20, rootFontSize: 32), 9);
    }

    [Fact]
    public void Viewport_Units_Resolve_Against_The_Viewport()
    {
        Assert.Equal(20, Resolve("10vw", SvgLengthAxis.Other, 200, 100), 9);
        Assert.Equal(10, Resolve("10vh", SvgLengthAxis.Other, 200, 100), 9);
        Assert.Equal(10, Resolve("10vmin", SvgLengthAxis.Other, 200, 100), 9);
        Assert.Equal(20, Resolve("10vmax", SvgLengthAxis.Other, 200, 100), 9);
    }

    [Fact]
    public void Percentages_Resolve_Against_The_Axis()
    {
        Assert.Equal(100, Resolve("50%", SvgLengthAxis.Horizontal, 200, 100), 9);
        Assert.Equal(50, Resolve("50%", SvgLengthAxis.Vertical, 200, 100), 9);

        // The 'other' axis resolves against the normalized diagonal.
        var diagonal = Math.Sqrt((200.0 * 200 + 100.0 * 100) / 2);
        Assert.Equal(diagonal / 2, Resolve("50%", SvgLengthAxis.Other, 200, 100), 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10foo")]
    [InlineData("10 20")]
    // CSS units no major renderer supports in this context stay rejected, which
    // drops the attribute per the error-handling rules.
    [InlineData("10cap")]
    [InlineData("10ic")]
    [InlineData("10lh")]
    [InlineData("10rlh")]
    [InlineData("10vi")]
    [InlineData("10vb")]
    public void Invalid_Input_Fails(string input)
    {
        Assert.False(SvgLength.TryParse(input.AsSpan(), out _));
    }
}
