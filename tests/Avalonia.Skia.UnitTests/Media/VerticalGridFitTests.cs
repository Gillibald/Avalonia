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

        [Fact]
        public void Crossbars_Render_Thickness_True_And_Crisp()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['e'];
            var scratch = new GlyphPathBuilder();
            var probed = false;

            for (var size = 11.0; size <= 16.0; size += 0.5)
            {
                var scaleQ = GlyphMaskKey.QuantizeScale((float)size);
                var scale = (float)size / typeface.Metrics.DesignEmHeight;

                scratch.Reset();
                Assert.True(typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), scratch));

                var zones = typeface.GridFit.GetWarp(scaleQ);

                var standards = typeface.StemWidths.HorizontalStrokeWidths;
                var designToPixels = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);
                Span<float> knotFrom = stackalloc float[16];
                Span<float> knotTo = stackalloc float[16];
                var knots = StemFit.CollectStrokeKnots(scratch, zones.From, 0.75f,
                    standards, designToPixels, knotFrom, knotTo);

                if (knots < 2)
                {
                    continue;
                }

                // Thickness-true under unification: a pair within the cut-in of a font-wide
                // standard renders at the standard's rounded width, otherwise at its own.
                for (var k = 0; k + 1 < knots; k += 2)
                {
                    var raw = knotFrom[k + 1] - knotFrom[k];
                    var snapped = knotTo[k + 1] - knotTo[k];
                    var expected = Math.Max(1, Math.Round(raw));
                    var bestDistance = float.MaxValue;

                    foreach (var standard in standards)
                    {
                        var standardPx = standard * designToPixels;

                        if (Math.Abs(raw - standardPx) < bestDistance)
                        {
                            bestDistance = Math.Abs(raw - standardPx);
                            expected = Math.Max(1, Math.Round(standardPx));
                        }
                    }

                    if (bestDistance > 1f)
                    {
                        expected = Math.Max(1, Math.Round(raw));
                    }

                    Assert.Equal(expected, snapped, 3);
                }

                // Probe the bar: wherever the unwarped raster smears it over two partial
                // rows, the fit must render it as hard rows.
                var rawThickness = knotFrom[1] - knotFrom[0];

                var fit = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased));

                scratch.Reset();
                Assert.True(typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), scratch));

                var raster = new byte[fit.Width * fit.Height];

                GlyphRasterizer.Rasterize(scratch, fit.Width, fit.Height, -fit.Left, -fit.Top, false, raster);

                // Count partial rows in the bar band only (one pixel of margin around it).
                var bandTop = (int)Math.Floor(knotFrom[0]) - 1;
                var bandBottom = (int)Math.Ceiling(knotFrom[1]) + 1;
                var column = fit.Width / 2;

                var fitPartials = CountPartialRows(fit.Alpha, fit.Width, fit.Height, fit.Top, column, bandTop, bandBottom);
                var rawPartials = CountPartialRows(raster, fit.Width, fit.Height, fit.Top, column, bandTop, bandBottom);

                if (rawPartials < 2)
                {
                    continue;   // this size did not smear — not a probing case
                }

                probed = true;

                Assert.True(fitPartials <= 1,
                    $"{size}px: bar {rawThickness:0.00}px renders {fitPartials} partial rows after the fit (raw {rawPartials}) — washed");
            }

            Assert.True(probed, "no probing size found for the e crossbar");
        }

        private static int CountPartialRows(byte[] alpha, int width, int height, int top,
            int column, int bandTop, int bandBottom)
        {
            var partials = 0;

            for (var y = 0; y < height; y++)
            {
                var deviceRow = top + y;

                if (deviceRow < bandTop || deviceRow > bandBottom)
                {
                    continue;
                }

                var coverage = alpha[y * width + column];

                if (coverage is > 40 and < 216)
                {
                    partials++;
                }
            }

            return partials;
        }

        [Fact]
        public void Colliding_Cap_And_Ascender_Merge_Toward_The_Ascender_Row()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Needs Segoe UI's near-colliding cap and ascender zones.");

            // Segoe UI puts caps at 1434 and the ascender at 1516 design units - 0.36 to 0.48
            // px apart at 9-12 px, inside the collision window. Hinted DirectWrite output
            // resolves every such size to ONE row at plain nearest of the ascender (9 px:
            // round(6.66) = 7). Resolving toward the cap instead renders capitals a row
            // shorter than l and f on the same line, which reads as ragged tops.
            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));

            Assert.True(typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap['l'], out var ascenderBox));

            foreach (var size in new[] { 9f, 10f })
            {
                var ascenderPx = ascenderBox.YMax * size / typeface.Metrics.DesignEmHeight;
                var expectedTop = -(int)MathF.Floor(ascenderPx + 0.5f);

                foreach (var reference in "Hl8f")
                {
                    var top = TopInkRow(typeface, reference, size);

                    Assert.True(top == expectedTop,
                        $"'{reference}' at {size}px: line top row {top}, expected {expectedTop}");
                }
            }

            // Past the window (0.64 px apart at 16 px) the lines split, again matching the
            // hinted output: caps 11 rows, ascenders 12.
            Assert.Equal(-11, TopInkRow(typeface, 'H', 16));
            Assert.Equal(-12, TopInkRow(typeface, 'l', 16));
            Assert.Equal(-12, TopInkRow(typeface, 'f', 16));
        }

        [Fact]
        public void F_Hook_Overshoot_Flattens_Onto_The_Ascender_Row()
        {
            var typeface = LoadTypeface();

            // Inter's f reaches 96/2816 em above l. At 12 px that is 0.41 px - visually the
            // same line - so the hook must flatten onto the ascender row and render hard,
            // not drift into a soft partial row above it.
            var fTop = TopInkRow(typeface, 'f', 12);
            var lTop = TopInkRow(typeface, 'l', 12);

            Assert.True(fTop == lTop, $"f tops at {fTop}, l at {lTop} - ragged ascender line");

            // The hook is an arc, so its top row can never be stem-hard - but flattened onto
            // the line it must read as part of it, not as a faint sliver above.
            var mask = BuildMask(typeface, 'f', 12);

            Assert.True(TopRowMax(mask.Alpha, mask.Width, mask.Height).Max >= 128,
                "the flattened f top must contribute visibly to the ascender line");
        }

        [Fact]
        public void Coincident_Cap_And_Ascender_Keep_The_Grow_Policy()
        {
            var typeface = LoadTypeface();

            // Inter puts caps and the ascender at the same design height (2048) - one line,
            // not a collision. At 13 px it lands at 9.45 px, inside the grow window: the
            // merged-line nearest rule must NOT apply, or the whole line loses a row against
            // the calibrated grow policy.
            foreach (var reference in "Hlf")
            {
                var top = TopInkRow(typeface, reference, 13);

                Assert.True(top == -10, $"'{reference}' at 13px: line top row {top}, expected -10 (grow)");
            }
        }

        /// <summary>Topmost visible ink row of the glyph's grid-fit mask, baseline-relative
        /// (negative above); visible means any pixel over 40/255 coverage.</summary>
        private static int TopInkRow(GlyphTypeface typeface, char reference, float size)
        {
            var mask = BuildMask(typeface, reference, size);

            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width; x++)
                {
                    if (mask.Alpha[y * mask.Width + x] > 40)
                    {
                        return mask.Top + y;
                    }
                }
            }

            return 0;
        }

        private static GlyphMask BuildMask(GlyphTypeface typeface, char reference, float size)
        {
            var scratch = new GlyphPathBuilder();

            return GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(typeface.CharacterToGlyphMap[reference], GlyphMaskKey.QuantizeScale(size), 0,
                    GlyphMaskMode.Antialiased));
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
