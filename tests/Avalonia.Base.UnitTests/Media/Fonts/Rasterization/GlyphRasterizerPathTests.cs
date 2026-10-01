using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// The rasterizer's vector paths accumulate and resolve coverage with the scalar path's
    /// operations in the scalar path's order, so every path produces the same bytes. The oracle
    /// is the scalar path itself, over glyph outlines of several fonts (quadratic and cubic,
    /// Latin, Arabic and CJK) at several sizes, transforms and phases, masks clipped on every
    /// side, both fill rules, aliased coverage, and synthetic paths with degenerate, huge and
    /// non-finite coordinates.
    /// </summary>
    public class GlyphRasterizerPathTests
    {
        private static readonly string[] s_fonts =
        {
            "Inter-Regular.ttf", "NotoSans-Italic.ttf", "SourceCodePro-Subset.otf", "NISC18030.ttf",
            "NotoSansArabic-Regular.ttf",
        };

        private static readonly double[] s_sizes = { 6, 11, 17, 24, 41, 97 };

        private static readonly Matrix[] s_transforms =
        {
            Matrix.Identity,
            Matrix.CreateRotation(Math.PI * 17 / 180),
            Matrix.CreateRotation(-Math.PI * 63 / 180),
            Matrix.CreateSkew(0.3, 0),
            Matrix.CreateScale(1.6, 0.7),
        };

        private static readonly (float X, float Y)[] s_phases = { (0, 0), (0.25f, 0.5f), (0.75f, 0.25f) };

        public static IEnumerable<object[]> VectorPaths()
        {
            yield return new object[] { nameof(GlyphRasterizerPath.Vector128) };
            yield return new object[] { nameof(GlyphRasterizerPath.Vector256) };
            yield return new object[] { nameof(GlyphRasterizerPath.Portable) };
        }

        [Theory]
        [MemberData(nameof(VectorPaths))]
        public void Glyph_Outlines_Rasterize_To_The_Scalar_Bytes(string pathName)
        {
            var path = Enum.Parse<GlyphRasterizerPath>(pathName);

            SkipUnlessSupported(path);

            var cases = 0;

            foreach (var font in s_fonts)
            {
                Assert.True(SfntFace.TryLoad(new MemoryStream(TestFontFiles.Load(font)), out var face), font);

                var typeface = new GlyphTypeface(face);
                var step = Math.Max(1, typeface.GlyphCount / 37);

                for (var glyph = 1; glyph < typeface.GlyphCount; glyph += step)
                {
                    foreach (var size in s_sizes)
                    {
                        foreach (var transform in s_transforms)
                        {
                            foreach (var phase in s_phases)
                            {
                                cases += CompareGlyph(path, typeface, (ushort)glyph, size, transform, phase, font);
                            }
                        }
                    }
                }
            }

            Assert.True(cases > 10000, $"only {cases} glyph masks compared");
        }

        [Theory]
        [MemberData(nameof(VectorPaths))]
        public void Synthetic_Paths_Rasterize_To_The_Scalar_Bytes(string pathName)
        {
            var path = Enum.Parse<GlyphRasterizerPath>(pathName);

            SkipUnlessSupported(path);

            var random = new Random(20261001);

            for (var i = 0; i < 4000; i++)
            {
                var builder = new GlyphPathBuilder();
                var width = 1 + random.Next(i % 7 == 0 ? 90 : 33);
                var height = 1 + random.Next(i % 5 == 0 ? 70 : 29);
                var figures = 1 + random.Next(4);

                if (random.Next(3) == 0)
                {
                    builder.SetFillRule(FillRule.EvenOdd);
                }

                for (var figure = 0; figure < figures; figure++)
                {
                    builder.BeginFigure(RandomPoint(random, width, height, i));

                    var segments = 1 + random.Next(9);

                    for (var s = 0; s < segments; s++)
                    {
                        switch (random.Next(3))
                        {
                            case 0:
                                builder.LineTo(RandomPoint(random, width, height, i));
                                break;
                            case 1:
                                builder.QuadraticBezierTo(RandomPoint(random, width, height, i),
                                    RandomPoint(random, width, height, i));
                                break;
                            default:
                                builder.CubicBezierTo(RandomPoint(random, width, height, i),
                                    RandomPoint(random, width, height, i), RandomPoint(random, width, height, i));
                                break;
                        }
                    }

                    builder.EndFigure(true);
                }

                var offsetX = (float)(random.NextDouble() * 4 - 2);
                var offsetY = (float)(random.NextDouble() * 4 - 2);

                foreach (var aliased in new[] { false, true })
                {
                    AssertSameBytes(path, builder, width, height, offsetX, offsetY, aliased, $"synthetic {i}");
                }
            }
        }

        /// <summary>
        /// Rasterizes the glyph into a mask fitted to its transformed outline and into one cut
        /// short on every side, antialiased with both fill rules and aliased, and compares each
        /// with the scalar path. Returns the number of masks compared.
        /// </summary>
        private static int CompareGlyph(GlyphRasterizerPath path, GlyphTypeface typeface, ushort glyph, double size,
            Matrix transform, (float X, float Y) phase, string font)
        {
            var scale = size / typeface.Metrics.DesignEmHeight;
            var design = new Matrix(scale, 0, 0, -scale, 0, 0) * new Matrix(transform.M11, transform.M12,
                transform.M21, transform.M22, 0, 0);
            var builder = new GlyphPathBuilder();

            if (!typeface.TryBuildGlyphContours(glyph, design, builder) || builder.Points.Length == 0)
            {
                return 0;
            }

            var points = builder.Points;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

            for (var i = 0; i < points.Length; i += 2)
            {
                minX = Math.Min(minX, points[i]);
                maxX = Math.Max(maxX, points[i]);
                minY = Math.Min(minY, points[i + 1]);
                maxY = Math.Max(maxY, points[i + 1]);
            }

            var left = (int)Math.Floor(minX) - 1;
            var top = (int)Math.Floor(minY) - 1;
            var width = (int)Math.Ceiling(maxX) + 1 - left;
            var height = (int)Math.Ceiling(maxY) + 1 - top;
            var label = FormattableString.Invariant($"{font} glyph {glyph} {size}px {transform} phase {phase}");

            AssertSameBytes(path, builder, width, height, -left + phase.X, -top + phase.Y, false, label);
            AssertSameBytes(path, builder, width, height, -left + phase.X, -top + phase.Y, true, label + " aliased");

            var compared = 2;

            if (width > 6 && height > 6)
            {
                var cut = Math.Max(1, Math.Min(width, height) / 4);

                AssertSameBytes(path, builder, width - 2 * cut, height - 2 * cut, -left - cut + phase.X,
                    -top - cut + phase.Y, false, label + " clipped");
                compared++;
            }

            builder.SetFillRule(FillRule.EvenOdd);
            AssertSameBytes(path, builder, width, height, -left + phase.X, -top + phase.Y, false, label + " even-odd");

            return compared + 1;
        }

        private static Point RandomPoint(Random random, int width, int height, int index)
        {
            switch (random.Next(index % 11 == 0 ? 12 : 40))
            {
                case 0:
                    return new Point(double.NaN, random.NextDouble() * height);
                case 1:
                    return new Point(random.NextDouble() * width, double.PositiveInfinity);
                case 2:
                    return new Point(random.NextDouble() * 1e9 - 5e8, random.NextDouble() * height);
                case 3:
                    // Integer-aligned and exactly vertical neighbours.
                    return new Point(random.Next(-2, width + 3), random.Next(-2, height + 3));
                default:
                    return new Point(random.NextDouble() * (width + 8) - 4, random.NextDouble() * (height + 8) - 4);
            }
        }

        private static void AssertSameBytes(GlyphRasterizerPath path, GlyphPathBuilder builder, int width, int height,
            float offsetX, float offsetY, bool aliased, string label)
        {
            // Rows are written at a stride wider than the mask, and the bytes between the rows
            // must survive.
            var stride = width + 3;
            var expected = new byte[stride * height];
            var actual = new byte[stride * height];

            expected.AsSpan().Fill(0xA5);
            actual.AsSpan().Fill(0xA5);

            var previous = GlyphRasterizer.Path;

            try
            {
                GlyphRasterizer.Path = GlyphRasterizerPath.Scalar;
                GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, aliased, expected, stride);

                GlyphRasterizer.Path = path;
                GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, aliased, actual, stride);
            }
            finally
            {
                GlyphRasterizer.Path = previous;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.Fail(FormattableString.Invariant(
                        $"{label}, {width}x{height}: byte ({i % stride}, {i / stride}) is {actual[i]} on {path}, {expected[i]} on the scalar path"));
                }
            }
        }

        private static void SkipUnlessSupported(GlyphRasterizerPath path)
            => Assert.SkipUnless(GlyphRasterizer.IsSupported(path), $"{path} is not supported on this machine.");
    }
}
