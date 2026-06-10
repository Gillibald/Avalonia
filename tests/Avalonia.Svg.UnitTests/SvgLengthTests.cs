using System;
using Avalonia.Svg.Parsing;
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
    [InlineData("2em", 32)]
    [InlineData("2ex", 16)]
    [InlineData("1e1", 10)]
    public void Resolves_Absolute_Units(string input, double expected)
    {
        Assert.Equal(expected, Resolve(input), 9);
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
    public void Invalid_Input_Fails(string input)
    {
        Assert.False(SvgLength.TryParse(input.AsSpan(), out _));
    }
}
