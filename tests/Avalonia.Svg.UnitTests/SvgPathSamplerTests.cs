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
    public void Full_Circle_Arc_Samples_Stay_On_The_Circle()
    {
        // One arc command spanning a whole circle — the flattening must adapt
        // to the length or the samples collapse onto a coarse polygon.
        var sampler = SvgPathSampler.Parse("M 420 60 A 240 240 0 1 1 419.9 60".AsSpan());
        var radius = sampler.TotalLength / (2 * Math.PI);

        // The centroid of evenly spaced samples of a full circle is its center.
        var count = 0;
        double cx = 0, cy = 0;
        for (var s = 0.0; s < sampler.TotalLength; s += 1.0, count++)
        {
            Assert.True(sampler.TryGetPointAtLength(s, out var point, out _));
            cx += point.X;
            cy += point.Y;
        }

        cx /= count;
        cy /= count;

        for (var s = 0.0; s < sampler.TotalLength; s += 1.0)
        {
            sampler.TryGetPointAtLength(s, out var point, out _);
            var distance = Math.Sqrt((point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy));
            Assert.InRange(distance, radius - 0.1, radius + 0.1);
        }
    }

    [Fact]
    public void Full_Circle_Arc_Tangents_Are_Smooth()
    {
        var sampler = SvgPathSampler.Parse("M 420 60 A 240 240 0 1 1 419.9 60".AsSpan());

        // Walking the circle in glyph-sized strides, the tangent must advance
        // gradually (true rate: stride/radius ≈ 0.021 rad) instead of holding
        // still within a coarse chord and jumping at its end.
        const double stride = 5.0;
        var totalTurn = 0.0;
        sampler.TryGetPointAtLength(0, out _, out var previousAngle);
        for (var s = stride; s <= sampler.TotalLength - stride; s += stride)
        {
            Assert.True(sampler.TryGetPointAtLength(s, out _, out var angle));

            var delta = angle - previousAngle;
            while (delta > Math.PI)
                delta -= 2 * Math.PI;
            while (delta < -Math.PI)
                delta += 2 * Math.PI;

            Assert.InRange(Math.Abs(delta), 0.0, 0.06);
            totalTurn += delta;
            previousAngle = angle;
        }

        // The walk starts and ends a stride away from the path ends, so a
        // couple of strides' worth of turning is unaccounted for.
        Assert.InRange(Math.Abs(totalTurn), 2 * Math.PI - 4 * stride / 240, 2 * Math.PI + 0.05);
    }

    [Fact]
    public void Long_Cubic_Tangents_Are_Smooth()
    {
        // A single ~470-unit cubic: the old fixed 24-step flattening made
        // ~20-unit chords with stepped tangents.
        var sampler = SvgPathSampler.Parse("M 0 0 C 200 0 300 200 500 200".AsSpan());

        const double stride = 5.0;
        sampler.TryGetPointAtLength(0, out _, out var previousAngle);
        for (var s = stride; s <= sampler.TotalLength; s += stride)
        {
            Assert.True(sampler.TryGetPointAtLength(s, out _, out var angle));
            Assert.InRange(Math.Abs(angle - previousAngle), 0.0, 0.06);
            previousAngle = angle;
        }
    }

    [Fact]
    public void Transform_Rotates_Samples_And_Tangents()
    {
        // The referenced path's transform applies to the sampled output:
        // positions through the full matrix, tangents through its linear part.
        var sampler = SvgPathSampler.Parse("M 0 0 L 100 0".AsSpan(), Matrix.CreateRotation(Math.PI / 2));

        Assert.Equal(100, sampler.TotalLength, 9);
        Assert.True(sampler.TryGetPointAtLength(50, out var point, out var angle));
        Assert.Equal(0, point.X, 6);
        Assert.Equal(50, point.Y, 6);
        Assert.Equal(Math.PI / 2, angle, 6);

        var vertex = sampler.Vertices[0];
        Assert.NotNull(vertex.OutDirection);
        Assert.Equal(0, vertex.OutDirection!.Value.X, 6);
        Assert.Equal(1, vertex.OutDirection.Value.Y, 6);
    }

    [Fact]
    public void Transform_Scales_Measured_Lengths()
    {
        // Non-uniform scale: a semicircle of radius 50 squashed to half
        // height. The arc length is measured in transformed space — the
        // ellipse perimeter bounds are π·b·... loosely; assert it lands
        // between the squashed and unsquashed semicircle lengths.
        var sampler = SvgPathSampler.Parse(
            "M 0 0 A 50 50 0 0 1 100 0".AsSpan(), Matrix.CreateScale(1, 0.5));

        Assert.InRange(sampler.TotalLength, Math.PI * 50 * 0.5, Math.PI * 50);

        // The samples lie on the squashed ellipse: x in [0,100], y in [-25,0].
        for (var s = 0.0; s < sampler.TotalLength; s += 2)
        {
            Assert.True(sampler.TryGetPointAtLength(s, out var point, out _));
            Assert.InRange(point.X, -0.01, 100.01);
            Assert.InRange(point.Y, -25.01, 0.01);
        }
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
