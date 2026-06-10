using System;
using Avalonia.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgPathParserTests
{
    private static GeometrySink Parse(string data)
    {
        var sink = new GeometrySink();
        SvgPathParser.Parse(data.AsSpan(), sink);
        return sink;
    }

    [Fact]
    public void Parses_Absolute_Move_And_Line()
    {
        var sink = Parse("M 10 20 L 30 40");

        Assert.Equal(new[] { "M 10,20", "L 30,40", "End" }, sink.Operations);
    }

    [Fact]
    public void Parses_Relative_Move_And_Line()
    {
        var sink = Parse("m 10 20 l 5 5");

        Assert.Equal(new[] { "M 10,20", "L 15,25", "End" }, sink.Operations);
    }

    [Fact]
    public void Implicit_Coordinates_After_Moveto_Are_Linetos()
    {
        var sink = Parse("M 0 0 10 10 20 20");

        Assert.Equal(new[] { "M 0,0", "L 10,10", "L 20,20", "End" }, sink.Operations);
    }

    [Fact]
    public void Implicit_Coordinates_After_Relative_Moveto_Are_Relative_Linetos()
    {
        var sink = Parse("m 5 5 10 0 10 0");

        Assert.Equal(new[] { "M 5,5", "L 15,5", "L 25,5", "End" }, sink.Operations);
    }

    [Fact]
    public void Parses_Horizontal_And_Vertical_Lines()
    {
        var sink = Parse("M 0 0 H 10 V 5 h 2 v 3");

        Assert.Equal(new[] { "M 0,0", "L 10,0", "L 10,5", "L 12,5", "L 12,8", "End" }, sink.Operations);
    }

    [Fact]
    public void Smooth_Cubic_Reflects_Previous_Control_Point()
    {
        var sink = Parse("M 0 0 C 10 0 20 10 30 10 S 50 20 60 10");

        Assert.Equal(
            new[]
            {
                "M 0,0",
                "C 10,0 20,10 30,10",
                // Reflection of (20,10) about (30,10) is (40,10).
                "C 40,10 50,20 60,10",
                "End",
            },
            sink.Operations);
    }

    [Fact]
    public void Smooth_Cubic_Without_Previous_Cubic_Uses_Current_Point()
    {
        var sink = Parse("M 10 10 S 20 20 30 10");

        Assert.Equal(new[] { "M 10,10", "C 10,10 20,20 30,10", "End" }, sink.Operations);
    }

    [Fact]
    public void Smooth_Quadratic_Reflects_Previous_Control_Point()
    {
        var sink = Parse("M 0 0 Q 10 0 20 0 T 40 0");

        Assert.Equal(
            new[]
            {
                "M 0,0",
                "Q 10,0 20,0",
                // Reflection of (10,0) about (20,0) is (30,0).
                "Q 30,0 40,0",
                "End",
            },
            sink.Operations);
    }

    [Fact]
    public void Parses_Arc_With_Juxtaposed_Flags()
    {
        // "0150" is large-arc=0, sweep=1, then the number 50.
        var sink = Parse("M 0 0 a25 25 -30 0150 -25");

        Assert.Equal(
            new[]
            {
                "M 0,0",
                $"A 25,25 {(-30 * Math.PI / 180).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} 0 1 50,-25",
                "End",
            },
            sink.Operations);
    }

    [Fact]
    public void Arc_With_Zero_Radius_Degenerates_To_Line()
    {
        var sink = Parse("M 0 0 A 0 10 0 0 1 20 0");

        Assert.Equal(new[] { "M 0,0", "L 20,0", "End" }, sink.Operations);
    }

    [Fact]
    public void Close_Path_Ends_Closed_Figure()
    {
        var sink = Parse("M 0 0 L 10 0 L 10 10 Z");

        Assert.Equal(new[] { "M 0,0", "L 10,0", "L 10,10", "Z" }, sink.Operations);
    }

    [Fact]
    public void Drawing_After_Close_Starts_New_Subpath_At_Start_Point()
    {
        var sink = Parse("M 0 0 L 10 0 Z L 5 5");

        Assert.Equal(new[] { "M 0,0", "L 10,0", "Z", "M 0,0", "L 5,5", "End" }, sink.Operations);
    }

    [Fact]
    public void New_Moveto_Ends_Open_Figure()
    {
        var sink = Parse("M 0 0 L 1 0 M 10 10 L 11 10");

        Assert.Equal(new[] { "M 0,0", "L 1,0", "End", "M 10,10", "L 11,10", "End" }, sink.Operations);
    }

    [Fact]
    public void Parses_Scientific_Notation()
    {
        var sink = Parse("M 1e1 2E-1 L 1.5e2 0");

        Assert.Equal(new[] { "M 10,0.2", "L 150,0" , "End" }, sink.Operations);
    }

    [Fact]
    public void Parses_Compressed_Decimal_Numbers()
    {
        // "1.5.5" is the two numbers 1.5 and 0.5; "-1-2" is -1 and -2.
        var sink = Parse("M 1.5.5 L-1-2");

        Assert.Equal(new[] { "M 1.5,0.5", "L -1,-2", "End" }, sink.Operations);
    }

    [Fact]
    public void Malformed_Input_Throws_After_Emitting_Valid_Prefix()
    {
        var sink = new GeometrySink();

        Assert.Throws<FormatException>(() => SvgPathParser.Parse("M 0 0 L 10 10 L 20".AsSpan(), sink));

        // The valid prefix was emitted and the open figure was ended, so the
        // partial geometry is renderable per the SVG error-handling rules.
        Assert.Equal(new[] { "M 0,0", "L 10,10", "End" }, sink.Operations);
    }

    [Fact]
    public void Path_Not_Starting_With_Moveto_Throws()
    {
        var sink = new GeometrySink();

        Assert.Throws<FormatException>(() => SvgPathParser.Parse("L 10 10".AsSpan(), sink));
        Assert.Empty(sink.Operations);
    }

    [Fact]
    public void Empty_Input_Produces_No_Operations()
    {
        var sink = Parse("   ");

        Assert.Empty(sink.Operations);
    }
}
