using System;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class GlyphRasterizerTests
    {
        private static byte[] Render(Action<IGeometryContext> draw, int width, int height,
            bool aliased = false, FillRule? fillRule = null, float offsetX = 0, float offsetY = 0)
        {
            var builder = new GlyphPathBuilder();

            if (fillRule is { } rule)
            {
                builder.SetFillRule(rule);
            }

            draw(builder);

            var destination = new byte[width * height];
            GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, aliased, destination);
            return destination;
        }

        private static void Rect(IGeometryContext context, double x0, double y0, double x1, double y1,
            bool reverse = false)
        {
            if (!reverse)
            {
                context.BeginFigure(new Point(x0, y0));
                context.LineTo(new Point(x1, y0));
                context.LineTo(new Point(x1, y1));
                context.LineTo(new Point(x0, y1));
            }
            else
            {
                context.BeginFigure(new Point(x0, y0));
                context.LineTo(new Point(x0, y1));
                context.LineTo(new Point(x1, y1));
                context.LineTo(new Point(x1, y0));
            }

            context.EndFigure(true);
        }

        private static double TotalCoverage(byte[] mask) => mask.Sum(b => b) / 255.0;

        [Fact]
        public void Integer_Aligned_Rect_Covers_Interior_Exactly()
        {
            var mask = Render(c => Rect(c, 2, 2, 6, 6), 8, 8);

            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var expected = x >= 2 && x < 6 && y >= 2 && y < 6 ? (byte)255 : (byte)0;
                    Assert.Equal(expected, mask[y * 8 + x]);
                }
            }
        }

        [Fact]
        public void Half_Pixel_Vertical_Edge_Is_Half_Covered()
        {
            var mask = Render(c => Rect(c, 1.5, 0, 3, 4), 5, 4);

            for (var y = 0; y < 4; y++)
            {
                Assert.Equal(0, mask[y * 5 + 0]);
                Assert.Equal(128, mask[y * 5 + 1]);
                Assert.Equal(255, mask[y * 5 + 2]);
                Assert.Equal(0, mask[y * 5 + 3]);
            }
        }

        [Fact]
        public void NonZero_Hole_With_Opposite_Winding_Renders_Empty()
        {
            var mask = Render(c =>
            {
                Rect(c, 1, 1, 7, 7);
                Rect(c, 3, 3, 5, 5, reverse: true);
            }, 8, 8);

            // Ring is filled, hole is empty — the counter shape of every 'O'.
            Assert.Equal(255, mask[2 * 8 + 2]);
            Assert.Equal(255, mask[5 * 8 + 2]);
            Assert.Equal(0, mask[4 * 8 + 4]);
            Assert.Equal(0, mask[3 * 8 + 3]);
        }

        [Fact]
        public void NonZero_Same_Winding_Overlap_Saturates()
        {
            var mask = Render(c =>
            {
                Rect(c, 1, 1, 5, 5);
                Rect(c, 3, 3, 7, 7);
            }, 8, 8);

            // Winding 2 in the overlap still reads as full coverage, not wrap-around.
            Assert.Equal(255, mask[4 * 8 + 4]);
            Assert.Equal(255, mask[2 * 8 + 2]);
            Assert.Equal(255, mask[6 * 8 + 6]);
        }

        [Fact]
        public void EvenOdd_Same_Winding_Overlap_Cancels()
        {
            var mask = Render(c =>
            {
                Rect(c, 1, 1, 5, 5);
                Rect(c, 3, 3, 7, 7);
            }, 8, 8, fillRule: FillRule.EvenOdd);

            Assert.Equal(0, mask[4 * 8 + 4]);
            Assert.Equal(255, mask[2 * 8 + 2]);
            Assert.Equal(255, mask[6 * 8 + 6]);
        }

        [Fact]
        public void Degenerate_Paths_Produce_No_Coverage()
        {
            Assert.All(Render(_ => { }, 4, 4), b => Assert.Equal(0, b));

            Assert.All(Render(c =>
            {
                c.BeginFigure(new Point(1, 1));
                c.EndFigure(true);
            }, 4, 4), b => Assert.Equal(0, b));

            Assert.All(Render(c => Rect(c, 0, 2, 4, 2), 4, 4), b => Assert.Equal(0, b));
        }

        [Fact]
        public void Aliased_Mode_Thresholds_Coverage_At_Half()
        {
            var mask = Render(c => Rect(c, 1.75, 0, 3, 4), 5, 4, aliased: true);

            for (var y = 0; y < 4; y++)
            {
                // Column 1 is 25% covered — below the threshold; column 2 is fully covered.
                Assert.Equal(0, mask[y * 5 + 1]);
                Assert.Equal(255, mask[y * 5 + 2]);
            }
        }

        [Fact]
        public void Quadratic_Segment_Area_Matches_The_Analytic_Value()
        {
            // Region enclosed by a quadratic and its chord has 2/3 of the control triangle's
            // area: triangle (1,5) (3,1) (5,5) has area 8, so the region encloses 16/3 ≈ 5.33.
            var mask = Render(c =>
            {
                c.BeginFigure(new Point(1, 5));
                c.QuadraticBezierTo(new Point(3, 1), new Point(5, 5));
                c.EndFigure(true);
            }, 6, 6);

            var area = TotalCoverage(mask);

            // Flattening inscribes the convex side, so coverage may only undershoot, and by no
            // more than the tolerance integrated along the curve (n = 3 pieces here predicts a
            // deficit of (16/3) / n² ≈ 0.59). It must never overshoot the analytic area.
            Assert.InRange(area, 16.0 / 3 - 0.75, 16.0 / 3 + 0.1);
        }

        [Fact]
        public void Cubic_Segment_Area_Matches_The_Analytic_Value()
        {
            // For endpoints (1,5), (5,5) and controls (2,1), (4,1): y(t) = 5 - 12t + 12t² and
            // x(t) = 1 + 3t + 3t² - 2t³, so the area enclosed against the chord is
            // ∫ (5 - y) x' dt = 12 ∫ (3t + 3t² - 12t³ + 6t⁴) dt = 12 · 0.7 = 8.4.
            var mask = Render(c =>
            {
                c.BeginFigure(new Point(1, 5));
                c.CubicBezierTo(new Point(2, 1), new Point(4, 1), new Point(5, 5));
                c.EndFigure(true);
            }, 6, 6);

            var area = TotalCoverage(mask);

            // Same inscription property as the quadratic case: undershoot bounded by the
            // flattening tolerance (4 pieces predict ≈ 8.4 / 16 ≈ 0.53 of deficit), no overshoot.
            Assert.InRange(area, 8.4 - 0.75, 8.4 + 0.1);
        }

        [Fact]
        public void Geometry_Left_Of_The_Mask_Still_Fills_From_Column_Zero()
        {
            // The shape starts at x = -2; in-mask columns 0..2 must be fully covered anyway.
            var mask = Render(c => Rect(c, -2, 1, 3, 3), 4, 4);

            Assert.Equal(255, mask[1 * 4 + 0]);
            Assert.Equal(255, mask[1 * 4 + 2]);
            Assert.Equal(0, mask[1 * 4 + 3]);
            Assert.Equal(0, mask[0 * 4 + 0]);
        }

        [Fact]
        public void Rasterization_Is_Deterministic_For_A_Real_Glyph()
        {
            var (builder, width, height, offsetX, offsetY) = BuildDeviceGlyph('g', 48f);

            var first = new byte[width * height];
            var second = new byte[width * height];

            GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, false, first);
            GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, false, second);

            Assert.True(first.AsSpan().SequenceEqual(second));
            Assert.True(TotalCoverage(first) > 10);
        }

        [Fact]
        public void Coverage_Stays_Inside_The_Ink_Bounds_Mask()
        {
            foreach (var character in "AgQ@wj")
            {
                var (builder, width, height, offsetX, offsetY) = BuildDeviceGlyph(character, 32f, apron: 2);

                var mask = new byte[width * height];
                GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, false, mask);

                // With a 2px apron and at most ~1px of AA bleed past the control-point box, the
                // outermost ring must stay empty — coverage never escapes the ink-derived mask.
                for (var x = 0; x < width; x++)
                {
                    Assert.Equal(0, mask[x]);
                    Assert.Equal(0, mask[(height - 1) * width + x]);
                }

                for (var y = 0; y < height; y++)
                {
                    Assert.Equal(0, mask[y * width]);
                    Assert.Equal(0, mask[y * width + width - 1]);
                }

                Assert.True(TotalCoverage(mask) > 5, $"Glyph '{character}' rendered almost nothing.");
            }
        }

        [Fact]
        public void Subpixel_Phase_Shifts_Coverage_Without_Changing_Its_Total()
        {
            var (builder, width, height, offsetX, offsetY) = BuildDeviceGlyph('o', 24f, apron: 2);

            var phase0 = new byte[width * height];
            var phase1 = new byte[width * height];

            GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, false, phase0);
            GlyphRasterizer.Rasterize(builder, width, height, offsetX + 0.5f, offsetY, false, phase1);

            Assert.False(phase0.AsSpan().SequenceEqual(phase1));

            var total0 = TotalCoverage(phase0);
            var total1 = TotalCoverage(phase1);

            // The same outline shifted by half a pixel covers the same area, redistributed.
            Assert.InRange(total1, total0 * 0.99, total0 * 1.01);
        }

        private static (GlyphPathBuilder builder, int width, int height, float offsetX, float offsetY)
            BuildDeviceGlyph(char character, float pixelsPerEm, int apron = 2)
        {
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap[character];
            var scale = pixelsPerEm / typeface.Metrics.DesignEmHeight;

            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

            var left = box.XMin * scale;
            var right = box.XMax * scale;
            var top = -box.YMax * scale;
            var bottom = -box.YMin * scale;

            var maskLeft = (int)Math.Floor(left) - apron;
            var maskTop = (int)Math.Floor(top) - apron;
            var width = (int)Math.Ceiling(right) + apron - maskLeft;
            var height = (int)Math.Ceiling(bottom) + apron - maskTop;

            var builder = new GlyphPathBuilder();
            var transform = new Matrix(scale, 0, 0, -scale, 0, 0);

            Assert.True(typeface.TryBuildGlyphContours(glyph, transform, builder));

            return (builder, width, height, -maskLeft, -maskTop);
        }
    }
}
