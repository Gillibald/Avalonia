using System;
using Avalonia.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgPathSamplerTests
{
    [Fact]
    public void Measures_Line_Length()
    {
        var sampler = SvgPathSampler.Parse("M 0 0 L 100 0".AsSpan());

        Assert.Equal(100, sampler.TotalLength, 9);
    }

    [Fact]
    public void Samples_Point_And_Angle_Along_Line()
    {
        var sampler = SvgPathSampler.Parse("M 0 0 L 0 100".AsSpan());

        Assert.True(sampler.TryGetPointAtLength(50, out var point, out var angle));
        Assert.Equal(0, point.X, 9);
        Assert.Equal(50, point.Y, 9);
        Assert.Equal(Math.PI / 2, angle, 9);
    }

    [Fact]
    public void Measures_Arc_Length_Approximately()
    {
        // A semicircle of radius 50: length ≈ π·50 (fixed-step flattening
        // undershoots slightly).
        var sampler = SvgPathSampler.Parse("M 0 0 A 50 50 0 0 1 100 0".AsSpan());

        Assert.Equal(Math.PI * 50, sampler.TotalLength, 0);
    }

    [Fact]
    public void Vertices_Carry_Tangent_Directions()
    {
        var sampler = SvgPathSampler.Parse("M 0 0 L 10 0 L 10 10".AsSpan());

        Assert.Equal(3, sampler.Vertices.Count);

        var start = sampler.Vertices[0];
        Assert.Null(start.InDirection);
        Assert.Equal(0, start.Angle, 9);

        var mid = sampler.Vertices[1];
        // Bisector of +x and +y is 45°.
        Assert.Equal(Math.PI / 4, mid.Angle, 9);

        var end = sampler.Vertices[2];
        Assert.Null(end.OutDirection);
        Assert.Equal(Math.PI / 2, end.Angle, 9);
    }

    [Fact]
    public void Closed_Figure_Merges_Closure_Into_Start_Vertex()
    {
        // A unit square: the start vertex bisects the closing segment (+y arriving)
        // and the first segment (+x leaving).
        var sampler = SvgPathSampler.Parse("M 0 0 L 10 0 L 10 10 L 0 10 Z".AsSpan());

        Assert.Equal(4, sampler.Vertices.Count);
        var start = sampler.Vertices[0];
        Assert.NotNull(start.InDirection);
        Assert.NotNull(start.OutDirection);
        // In: (0,-1) up the left edge; out: (1,0). Bisector: -45°.
        Assert.Equal(-Math.PI / 4, start.Angle, 9);
    }

    [Fact]
    public void Cubic_Tangents_Come_From_Control_Points()
    {
        var sampler = SvgPathSampler.Parse("M 0 0 C 0 10 10 20 20 20".AsSpan());

        var start = sampler.Vertices[0];
        Assert.Equal(Math.PI / 2, start.Angle, 9); // towards (0,10): straight down (+y)

        var end = sampler.Vertices[1];
        Assert.Equal(0, end.Angle, 9); // from (10,20) to (20,20): +x
    }

    [Fact]
    public void Malformed_Path_Samples_Valid_Prefix()
    {
        var sampler = SvgPathSampler.Parse("M 0 0 L 100 0 L".AsSpan());

        Assert.Equal(100, sampler.TotalLength, 9);
    }
}
