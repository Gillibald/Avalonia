using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Vertical grid fitting: measured zones, warp knots landing on pixel rows, monotonicity,
    /// overshoot flattening, and the payoff — the x-height top of a mask renders as one hard
    /// row where the unwarped rasterization smears it across two.
    /// </summary>
    public class VerticalGridFitTests
    {
        [Fact]
        public void Zones_Are_Measured_In_The_Expected_Order()
        {
            var typeface = LoadTypeface();
            var map = typeface.CharacterToGlyphMap;

            Assert.True(typeface.TryGetGlyphInkBounds(map['x'], out var x));
            Assert.True(typeface.TryGetGlyphInkBounds(map['H'], out var h));
            Assert.True(typeface.TryGetGlyphInkBounds(map['l'], out var l));
            Assert.True(typeface.TryGetGlyphInkBounds(map['p'], out var p));
            Assert.True(typeface.TryGetGlyphInkBounds(map['o'], out var o));

            // The reference glyphs must produce the classic ordering; the warp relies on it.
            Assert.True(x.YMax > 0);
            Assert.True(h.YMax > x.YMax);
            Assert.True(l.YMax >= h.YMax);
            Assert.True(p.YMin < 0);
            Assert.True(o.YMax >= x.YMax);   // round overshoot (or flat-equal)
        }

        [Fact]
        public void Warp_Lands_Every_Zone_On_A_Pixel_Row()
        {
            var typeface = LoadTypeface();
            var map = typeface.CharacterToGlyphMap;
            var scaleQ = GlyphMaskKey.QuantizeScale(13);
            var warp = typeface.GridFit.GetWarp(scaleQ);

            Assert.False(warp.IsIdentity);

            var scale = 13f / typeface.Metrics.DesignEmHeight;

            Assert.True(typeface.TryGetGlyphInkBounds(map['x'], out var x));
            Assert.True(typeface.TryGetGlyphInkBounds(map['H'], out var h));

            foreach (var zone in new float[] { -x.YMax * scale, -h.YMax * scale, 0 })
            {
                var snapped = warp.Apply(zone);

                Assert.True(Math.Abs(snapped - MathF.Round(snapped)) < 0.01f,
                    $"zone {zone} maps to {snapped}, not a pixel row");
            }

            // The baseline is the anchor: it must not move.
            Assert.Equal(0, warp.Apply(0), 3);
        }

        [Fact]
        public void The_Warp_Is_Monotone_And_Identity_Far_Outside()
        {
            var typeface = LoadTypeface();
            var warp = typeface.GridFit.GetWarp(GlyphMaskKey.QuantizeScale(13));

            var previous = float.MinValue;

            for (var y = -40f; y <= 40f; y += 0.25f)
            {
                var mapped = warp.Apply(y);

                Assert.True(mapped >= previous - 0.0001f, $"warp not monotone at {y}");
                previous = mapped;
            }

            // Outside the outer knots the map is a pure shift (slope one).
            Assert.Equal(warp.Apply(-200) - -200, warp.Apply(-300) - -300, 3);
            Assert.Equal(warp.Apply(200) - 200, warp.Apply(300) - 300, 3);
        }

        [Fact]
        public void Round_Overshoot_Flattens_Small_And_Survives_Large()
        {
            var typeface = LoadTypeface();
            var map = typeface.CharacterToGlyphMap;

            Assert.True(typeface.TryGetGlyphInkBounds(map['x'], out var x));
            Assert.True(typeface.TryGetGlyphInkBounds(map['o'], out var o));
            Assert.SkipWhen(o.YMax <= x.YMax, "Inter build has no measurable round overshoot.");

            var scale12 = 12f / typeface.Metrics.DesignEmHeight;
            var warp12 = typeface.GridFit.GetWarp(GlyphMaskKey.QuantizeScale(12));

            // At 12px the overshoot is far below a pixel: the o-top must land on the same row
            // as the x-height.
            Assert.Equal(warp12.Apply(-x.YMax * scale12), warp12.Apply(-o.YMax * scale12), 2);

            var scale200 = 200f / typeface.Metrics.DesignEmHeight;
            var warp200 = typeface.GridFit.GetWarp(GlyphMaskKey.QuantizeScale(200));

            // At 200px the overshoot is a visible design feature and must survive.
            Assert.True(warp200.Apply(-o.YMax * scale200) < warp200.Apply(-x.YMax * scale200) - 0.5f);
        }

        [Fact]
        public void The_X_Height_Top_Renders_One_Hard_Row_Where_Unwarped_Smears_Two()
        {
            var typeface = LoadTypeface();

            // 'z' has a flat top bar exactly at the x-height ('x' terminals are slanted in
            // Inter, so its top row can never fill even when perfectly snapped).
            var glyph = typeface.CharacterToGlyphMap['z'];
            var scale = 0.0;
            var size = 0.0;

            // Pick a size in the body range whose unwarped x-height is maximally fractional,
            // so the un-fit rasterization provably smears.
            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

            for (var candidate = 11.0; candidate <= 17.0; candidate += 0.5)
            {
                var s = candidate / typeface.Metrics.DesignEmHeight;
                var fraction = box.YMax * s - Math.Floor(box.YMax * s);

                if (fraction is > 0.35 and < 0.65)
                {
                    size = candidate;
                    scale = s;
                    break;
                }
            }

            Assert.True(size > 0, "no suitably fractional size found in the body range");

            var scratch = new GlyphPathBuilder();
            var warped = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale((float)size), 0, GlyphMaskMode.Antialiased));

            // The unwarped truth, built the way the rasterizer comparison suite does.
            scratch.Reset();
            Assert.True(typeface.TryBuildGlyphContours(glyph,
                new Matrix(scale, 0, 0, -scale, 0, 0), scratch));

            var width = warped.Width;
            var height = warped.Height;
            var raw = new byte[width * height];

            GlyphRasterizer.Rasterize(scratch, width, height, -warped.Left, -warped.Top, false, raw);

            // Top ink row of the unwarped mask: a fractional x-height means partial coverage.
            var rawTop = TopRowMax(raw, width, height);
            var warpedTop = TopRowMax(warped.Alpha, width, height);

            Assert.True(rawTop.Max < 210,
                $"expected a smeared unwarped top row at {size}px, got {rawTop.Max}");
            Assert.True(warpedTop.Max >= 240,
                $"expected a hard grid-fit top row at {size}px, got {warpedTop.Max}");
        }

        [Fact]
        public void Hinting_None_Bypasses_The_Warp_And_Keys_Separately()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['z'];

            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

            var size = 0.0;

            for (var candidate = 11.0; candidate <= 17.0; candidate += 0.5)
            {
                var s = candidate / typeface.Metrics.DesignEmHeight;
                var fraction = box.YMax * s - Math.Floor(box.YMax * s);

                if (fraction is > 0.35 and < 0.65)
                {
                    size = candidate;
                    break;
                }
            }

            Assert.True(size > 0, "no suitably fractional size found in the body range");

            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale((float)size);

            var fit = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased));
            var unfit = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, GridFit: false));

            // TextHintingMode.None means outlines scaled only: the top row keeps its smear.
            Assert.True(TopRowMax(unfit.Alpha, unfit.Width, unfit.Height).Max < 210);
            Assert.True(TopRowMax(fit.Alpha, fit.Width, fit.Height).Max >= 240);

            // And the two variants are distinct cache identities.
            Assert.NotEqual(
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased),
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, GridFit: false));
        }

        private static (int Row, int Max) TopRowMax(byte[] alpha, int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                var max = 0;

                for (var x = 0; x < width; x++)
                {
                    max = Math.Max(max, alpha[y * width + x]);
                }

                if (max > 16)
                {
                    return (y, max);
                }
            }

            return (-1, 0);
        }

        private static GlyphTypeface LoadTypeface()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf"));
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
        }
    }
}
