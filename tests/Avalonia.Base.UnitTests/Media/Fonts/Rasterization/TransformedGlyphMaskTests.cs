using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// Glyph masks built under a rotated, skewed or anisotropic device transform. Every
    /// expectation comes from geometry: the upright unhinted mask, exact quarter turns of it,
    /// the area of the flattened outline, and the typeface's own oblique simulation.
    /// </summary>
    public class TransformedGlyphMaskTests
    {
        private const string Glyphs = "gHaR@&%";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_Identity_Transform_Builds_The_Unhinted_Upright_Mask(bool aliased)
        {
            var mode = aliased ? GlyphMaskMode.Aliased : GlyphMaskMode.Antialiased;
            var typeface = LoadInter();
            var scratch = new GlyphPathBuilder();

            foreach (var c in Glyphs)
            {
                for (byte phase = 0; phase < GlyphMaskKey.PhaseCount; phase++)
                {
                    var key = new GlyphMaskKey(typeface.CharacterToGlyphMap[c], GlyphMaskKey.QuantizeScale(21.5f),
                        phase, mode, GridFit: false);

                    var upright = GlyphMasks.Build(typeface, scratch, key);
                    var transformed = GlyphMasks.BuildTransformed(typeface, scratch, key);

                    Assert.False(upright.IsEmpty);
                    AssertSameMask(upright, transformed, $"'{c}' phase {phase}");
                }
            }
        }

        [Theory]
        [InlineData(90)]
        [InlineData(180)]
        [InlineData(270)]
        public void Quarter_Turns_Are_Exact_Transposes_And_Flips_Of_The_Upright_Mask(int degrees)
        {
            var typeface = LoadInter();
            var scratch = new GlyphPathBuilder();
            var rotation = Matrix.CreateRotation(Math.PI * degrees / 180);

            foreach (var c in Glyphs)
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                var upright = GlyphMasks.BuildTransformed(typeface, scratch, CreateKey(glyph, 37f, Matrix.Identity));
                var rotated = GlyphMasks.BuildTransformed(typeface, scratch, CreateKey(glyph, 37f, rotation));

                Assert.False(upright.IsEmpty);
                Assert.False(rotated.IsEmpty);

                // Integer placement: every rotated pixel is exactly one upright pixel, found by
                // taking the rotated pixel's centre back through the inverse rotation.
                var inverse = rotation.Invert();
                var rotatedSum = 0L;

                for (var y = 0; y < rotated.Height; y++)
                {
                    for (var x = 0; x < rotated.Width; x++)
                    {
                        var centre = new Point(rotated.Left + x + 0.5, rotated.Top + y + 0.5).Transform(inverse);
                        var expected = Sample(upright, (int)Math.Floor(centre.X), (int)Math.Floor(centre.Y));
                        var actual = rotated.Alpha[y * rotated.Width + x];

                        Assert.True(expected == actual,
                            $"'{c}' at {degrees} degrees, pixel ({rotated.Left + x}, {rotated.Top + y}): " +
                            $"upright {expected}, rotated {actual}");

                        rotatedSum += actual;
                    }
                }

                // The comparison above visits rotated pixels only; equal totals prove no upright
                // ink fell outside the rotated mask.
                Assert.Equal(Sum(upright), rotatedSum);
            }
        }

        [Theory]
        [InlineData(17, 1, 3)]
        [InlineData(33, 2, 0)]
        [InlineData(-71, 3, 1)]
        [InlineData(142, 0, 2)]
        public void Arbitrary_Rotations_Cover_The_Flattened_Outline_Area_Inside_Its_Bounds(
            double degrees, byte phaseX, byte phaseY)
        {
            var typeface = LoadInter();
            var scratch = new GlyphPathBuilder();
            var rotation = Matrix.CreateRotation(Math.PI * degrees / 180);

            foreach (var c in "gHaR8O")
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                var key = CreateKey(glyph, 48f, rotation, phaseX, phaseY);
                var mask = GlyphMasks.BuildTransformed(typeface, scratch, key);

                Assert.False(mask.IsEmpty);

                // The outline exactly as the builder emits it, in mask-local pixels.
                var outline = new GlyphPathBuilder();

                Assert.True(typeface.TryBuildGlyphContours(glyph, DesignToDevice(typeface, key), outline));

                var offsetX = -mask.Left + key.PhaseOffset;
                var offsetY = -mask.Top + key.PhaseOffsetY;
                var polygons = Flatten(outline, offsetX, offsetY);

                var area = 0.0;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var polygon in polygons)
                {
                    area += ShoelaceArea(polygon);

                    foreach (var (px, py) in polygon)
                    {
                        minX = Math.Min(minX, px);
                        minY = Math.Min(minY, py);
                        maxX = Math.Max(maxX, px);
                        maxY = Math.Max(maxY, py);
                    }
                }

                area = Math.Abs(area);

                var coverage = 0.0;
                var partial = 0;

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var value = mask.Alpha[y * mask.Width + x];

                        if (value == 0)
                        {
                            continue;
                        }

                        coverage += value / 255.0;

                        if (value < 255)
                        {
                            partial++;
                        }

                        // Ink only where the pixel overlaps the outline's bounding box.
                        Assert.True(x + 1 > minX && x < maxX && y + 1 > minY && y < maxY,
                            $"'{c}': ink at ({x}, {y}) outside the outline bounds " +
                            $"[{minX:0.00}, {maxX:0.00}] x [{minY:0.00}, {maxY:0.00}]");
                    }
                }

                // The rasterizer integrates the flattened polygon exactly; storing coverage in
                // bytes rounds each partially covered pixel by at most half a level, and fully
                // covered or empty pixels are exact. The float accumulation adds noise orders of
                // magnitude below that, covered by a relative 1e-4.
                var tolerance = partial * (0.5 / 255) + area * 1e-4;

                Assert.True(Math.Abs(coverage - area) <= tolerance,
                    FormattableString.Invariant(
                        $"'{c}' at {degrees} degrees: coverage {coverage:0.000} px, outline area {area:0.000} px, tolerance {tolerance:0.000}"));
            }
        }

        [Fact]
        public void A_Skew_Equals_The_Oblique_Simulation_Of_The_Same_Shear()
        {
            var regular = LoadInter();
            var oblique = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .TryCreateGlyphTypeface(FontSimulations.Oblique);

            Assert.NotNull(oblique);

            var scratch = new GlyphPathBuilder();

            // The simulation shears design space x += slant * y (y up); in y-down device space
            // the same lean is x -= slant * y.
            var shear = new Matrix(1, 0, -FontSimulationConstants.ObliqueSlant, 1, 0, 0);
            const float pixelsPerEm = 64f;

            foreach (var c in Glyphs)
            {
                var glyph = regular.CharacterToGlyphMap[c];
                var skewKey = CreateKey(glyph, pixelsPerEm, shear);
                var skewed = GlyphMasks.BuildTransformed(regular, scratch, skewKey);
                var simulated = GlyphMasks.BuildTransformed(oblique!, scratch,
                    CreateKey(glyph, pixelsPerEm, Matrix.Identity, simulations: true));

                Assert.False(skewed.IsEmpty);
                Assert.False(simulated.IsEmpty);

                // The key quantizes the shear to 1/4096, which moves a point at height h by
                // |quantized - slant| * h; that bounds the coverage difference per pixel, plus
                // one level for rounding the two differently composed float transforms.
                var shearError = Math.Abs(skewKey.Transform.Skew21 + FontSimulationConstants.ObliqueSlant);
                var height = Math.Max(Math.Abs(skewed.Top), Math.Abs(skewed.Top + skewed.Height));
                var tolerance = (int)Math.Ceiling(255 * shearError * height) + 1;

                var left = Math.Min(skewed.Left, simulated.Left);
                var top = Math.Min(skewed.Top, simulated.Top);
                var right = Math.Max(skewed.Left + skewed.Width, simulated.Left + simulated.Width);
                var bottom = Math.Max(skewed.Top + skewed.Height, simulated.Top + simulated.Height);
                var worst = 0;

                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        worst = Math.Max(worst, Math.Abs(Sample(skewed, x, y) - Sample(simulated, x, y)));
                    }
                }

                Assert.True(worst <= tolerance, $"'{c}': worst difference {worst} levels, tolerance {tolerance}");
                Assert.InRange(Sum(skewed), Sum(simulated) * 0.99, Sum(simulated) * 1.01);
            }
        }

        internal static GlyphMaskKey CreateKey(ushort glyph, float pixelsPerEm, Matrix linear,
            byte phaseX = 0, byte phaseY = 0, bool simulations = false)
        {
            Assert.True(GlyphMaskTransform.TryQuantize(linear.M11, linear.M12, linear.M21, linear.M22,
                out var transform));

            return new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(pixelsPerEm), phaseX,
                GlyphMaskMode.Antialiased, GridFit: false, Transform: transform, PhaseY: phaseY,
                ApplySimulations: simulations);
        }

        /// <summary>The design-unit to mask-pixel transform the builder applies for a key.</summary>
        internal static Matrix DesignToDevice(GlyphTypeface typeface, in GlyphMaskKey key)
        {
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;
            var t = key.Transform;

            return new Matrix(scale * t.Scale11, scale * t.Skew12, -scale * t.Skew21, -scale * t.Scale22, 0, 0);
        }

        /// <summary>
        /// Flattens a captured path into closed polygons with the rasterizer's own piece counts,
        /// so the polygons are the ones it integrates.
        /// </summary>
        internal static List<List<(double X, double Y)>> Flatten(GlyphPathBuilder path, float offsetX, float offsetY)
        {
            var polygons = new List<List<(double X, double Y)>>();
            List<(double X, double Y)>? current = null;
            var verbs = path.Verbs;
            var points = path.Points;
            var p = 0;
            float curX = 0, curY = 0;

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        curX = points[p++] + offsetX;
                        curY = points[p++] + offsetY;
                        current = new List<(double X, double Y)> { (curX, curY) };
                        polygons.Add(current);
                        break;

                    case GlyphPathVerb.LineTo:
                        curX = points[p++] + offsetX;
                        curY = points[p++] + offsetY;
                        current!.Add((curX, curY));
                        break;

                    case GlyphPathVerb.QuadTo:
                    {
                        var cx = points[p++] + offsetX;
                        var cy = points[p++] + offsetY;
                        var x = points[p++] + offsetX;
                        var y = points[p++] + offsetY;
                        var n = GlyphRasterizer.QuadSegmentCount(curX, curY, cx, cy, x, y);

                        for (var i = 1; i <= n; i++)
                        {
                            var t = i / (double)n;
                            var mt = 1 - t;
                            current!.Add((mt * mt * curX + 2 * mt * t * cx + t * t * x,
                                mt * mt * curY + 2 * mt * t * cy + t * t * y));
                        }

                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.CubicTo:
                    {
                        var c1X = points[p++] + offsetX;
                        var c1Y = points[p++] + offsetY;
                        var c2X = points[p++] + offsetX;
                        var c2Y = points[p++] + offsetY;
                        var x = points[p++] + offsetX;
                        var y = points[p++] + offsetY;
                        var n = GlyphRasterizer.CubicSegmentCount(curX, curY, c1X, c1Y, c2X, c2Y, x, y);

                        for (var i = 1; i <= n; i++)
                        {
                            var t = i / (double)n;
                            var mt = 1 - t;
                            current!.Add((mt * mt * mt * curX + 3 * mt * mt * t * c1X + 3 * mt * t * t * c2X + t * t * t * x,
                                mt * mt * mt * curY + 3 * mt * mt * t * c1Y + 3 * mt * t * t * c2Y + t * t * t * y));
                        }

                        curX = x;
                        curY = y;
                        break;
                    }

                    case GlyphPathVerb.Close:
                        break;
                }
            }

            return polygons;
        }

        private static double ShoelaceArea(List<(double X, double Y)> polygon)
        {
            var sum = 0.0;

            for (var i = 0; i < polygon.Count; i++)
            {
                var (x0, y0) = polygon[i];
                var (x1, y1) = polygon[(i + 1) % polygon.Count];

                sum += x0 * y1 - x1 * y0;
            }

            return sum / 2;
        }

        internal static int Sample(GlyphMask mask, int deviceX, int deviceY)
        {
            var x = deviceX - mask.Left;
            var y = deviceY - mask.Top;

            return x < 0 || y < 0 || x >= mask.Width || y >= mask.Height ? 0 : mask.Alpha[y * mask.Width + x];
        }

        private static long Sum(GlyphMask mask)
        {
            var sum = 0L;

            foreach (var value in mask.Alpha)
            {
                sum += value;
            }

            return sum;
        }

        private static void AssertSameMask(GlyphMask expected, GlyphMask actual, string label)
        {
            Assert.True(expected.Left == actual.Left && expected.Top == actual.Top &&
                        expected.Width == actual.Width && expected.Height == actual.Height,
                $"{label}: placement ({actual.Left}, {actual.Top}, {actual.Width} x {actual.Height}) " +
                $"differs from ({expected.Left}, {expected.Top}, {expected.Width} x {expected.Height})");

            var index = actual.Alpha.AsSpan().CommonPrefixLength(expected.Alpha);

            Assert.True(index == expected.Alpha.Length,
                $"{label}: first difference at byte {index}");
        }

        internal static GlyphTypeface LoadInter()
        {
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).TryCreateGlyphTypeface();

            Assert.NotNull(typeface);

            return typeface!;
        }
    }
}
