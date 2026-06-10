using Avalonia.Media;
using Avalonia.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgColorTests
{
    private static Color Parse(string value)
    {
        Assert.True(SvgColor.TryParse(value, out var color), $"'{value}' should parse");
        return color;
    }

    [Fact]
    public void Hex_With_Alpha_Is_Rgba_Ordered()
    {
        // CSS puts alpha last; Avalonia's own parser reads it first.
        Assert.Equal(Color.FromArgb(0x88, 0x00, 0xFF, 0x00), Parse("#0f08"));
        Assert.Equal(Color.FromArgb(0x80, 0x00, 0xFF, 0x00), Parse("#00ff0080"));
    }

    [Theory]
    [InlineData("#0f0", 0xFF, 0x00, 0xFF, 0x00)]
    [InlineData("#00ff00", 0xFF, 0x00, 0xFF, 0x00)]
    [InlineData("green", 0xFF, 0x00, 0x80, 0x00)]
    [InlineData("GREEN", 0xFF, 0x00, 0x80, 0x00)]
    public void Plain_Colors_Delegate_To_Avalonia(string value, byte a, byte r, byte g, byte b)
    {
        Assert.Equal(Color.FromArgb(a, r, g, b), Parse(value));
    }

    [Theory]
    [InlineData("rgb(0, 128, 0)", 255, 0, 128, 0)]
    [InlineData("rgb(0 128 0)", 255, 0, 128, 0)]
    [InlineData("rgb(0.0, 127.6, 0.0)", 255, 0, 128, 0)]   // floats round
    [InlineData("rgb(0%, 50%, 0%)", 255, 0, 128, 0)]       // percentages scale
    [InlineData("rgb(-10, 300, 0)", 255, 0, 255, 0)]       // channels clamp
    [InlineData("rgba(0, 127, 0, -1)", 0, 0, 127, 0)]      // alpha clamps low
    [InlineData("rgba(0, 127, 0, 2)", 255, 0, 127, 0)]     // alpha clamps high
    [InlineData("rgba(0, 127, 0, 50%)", 128, 0, 127, 0)]   // percentage alpha
    public void Rgb_Follows_Css_Component_Rules(string value, byte a, byte r, byte g, byte b)
    {
        Assert.Equal(Color.FromArgb(a, r, g, b), Parse(value));
    }

    [Fact]
    public void Hsl_Hue_Wraps()
    {
        // 999° ≡ 279°; clamping to 360 would give red instead.
        Assert.Equal(Parse("hsl(279, 100%, 25%)"), Parse("hsl(999, 100%, 25%)"));
        Assert.Equal(Parse("hsl(240, 100%, 25%)"), Parse("hsl(-120, 100%, 25%)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("rgb()")]
    [InlineData("rgb(1, 2)")]
    [InlineData("rgb(0, 50%, 0)")] // channel units must not mix
    [InlineData("hsl(1, 2, 3)")] // s/l must be percentages
    public void Invalid_Colors_Fail(string value)
    {
        Assert.False(SvgColor.TryParse(value, out _));
    }
}
