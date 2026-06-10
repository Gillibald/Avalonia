using System;
using Avalonia.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgTransformParserTests
{
    private static Matrix Parse(string input)
    {
        Assert.True(SvgTransformParser.TryParse(input.AsSpan(), out var matrix), $"'{input}' should parse");
        return matrix;
    }

    private static void AssertPoint(Point expected, Point actual)
    {
        // Matrix.ToRadians is accurate to ~1e-8 over typical magnitudes.
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    [Fact]
    public void Parses_Translate()
    {
        var m = Parse("translate(10,20)");

        AssertPoint(new Point(11, 22), new Point(1, 2).Transform(m));
    }

    [Fact]
    public void Translate_Y_Defaults_To_Zero()
    {
        var m = Parse("translate(10)");

        AssertPoint(new Point(11, 2), new Point(1, 2).Transform(m));
    }

    [Fact]
    public void Parses_Scale_Uniform_And_NonUniform()
    {
        AssertPoint(new Point(2, 4), new Point(1, 2).Transform(Parse("scale(2)")));
        AssertPoint(new Point(2, 6), new Point(1, 2).Transform(Parse("scale(2 3)")));
    }

    [Fact]
    public void Parses_Rotate()
    {
        var m = Parse("rotate(90)");

        AssertPoint(new Point(0, 10), new Point(10, 0).Transform(m));
    }

    [Fact]
    public void Parses_Rotate_About_Center()
    {
        var m = Parse("rotate(90, 10, 10)");

        AssertPoint(new Point(20, 10), new Point(10, 0).Transform(m));
    }

    [Fact]
    public void Parses_SkewX()
    {
        var m = Parse("skewX(45)");

        // skewX maps (x, y) to (x + y·tan(a), y).
        AssertPoint(new Point(10, 10), new Point(0, 10).Transform(m));
    }

    [Fact]
    public void Parses_SkewY()
    {
        var m = Parse("skewY(45)");

        // skewY maps (x, y) to (x, y + x·tan(a)).
        AssertPoint(new Point(10, 10), new Point(10, 0).Transform(m));
    }

    [Fact]
    public void Parses_Matrix_In_Svg_Order()
    {
        var m = Parse("matrix(1 2 3 4 5 6)");

        Assert.Equal(new Matrix(1, 2, 3, 4, 5, 6), m);
    }

    [Fact]
    public void List_Applies_Rightmost_Transform_First()
    {
        var m = Parse("translate(10,0) scale(2)");

        // The point is scaled first, then translated.
        AssertPoint(new Point(12, 0), new Point(1, 0).Transform(m));
    }

    [Fact]
    public void Functions_May_Be_Comma_Separated()
    {
        var m = Parse("translate(10,0),scale(2)");

        AssertPoint(new Point(12, 0), new Point(1, 0).Transform(m));
    }

    [Theory]
    [InlineData("rotate(")]
    [InlineData("frobnicate(1)")]
    [InlineData("scale()")]
    [InlineData("matrix(1 2 3 4 5)")]
    [InlineData("translate(1) garbage")]
    public void Invalid_Input_Fails(string input)
    {
        Assert.False(SvgTransformParser.TryParse(input.AsSpan(), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Input_Is_Identity(string input)
    {
        Assert.True(SvgTransformParser.TryParse(input.AsSpan(), out var matrix));
        Assert.True(matrix.IsIdentity);
    }
}
