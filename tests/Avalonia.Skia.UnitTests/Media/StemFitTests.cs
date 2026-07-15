using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Horizontal stem snapping under Strong hinting: straight stems render as solid columns
    /// with hard edges, while curves and diagonals — where snapping would distort — stay
    /// byte-identical to the unsnapped build.
    /// </summary>
    public class StemFitTests
    {
        [Fact]
        public void H_Stems_Render_Solid_Columns_Where_Unsnapped_Smears()
        {
            var typeface = LoadTypeface();
            var glyph = typeface.CharacterToGlyphMap['H'];
            var scratch = new GlyphPathBuilder();

            // Find a body size where the stem edges land well off the grid, so the unsnapped
            // build provably smears its flanks.
            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));

            var size = 0.0;
            var probeRow = 0;

            for (var candidate = 11.0; candidate <= 20.0 && size == 0; candidate += 0.5)
            {
                var unfit = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale((float)candidate), 0, GlyphMaskMode.Antialiased));

                // Measure on a fixed device row just under the cap — well above the crossbar,
                // whose own interpolated edge would contaminate a mask-relative middle row.
                var capPx = (int)Math.Round(box.YMax * candidate / typeface.Metrics.DesignEmHeight);
                var deviceRow = -(capPx - 2);

                if (CountPartials(unfit, deviceRow - unfit.Top) >= 2)
                {
                    size = candidate;
                    probeRow = deviceRow;
                }
            }

            Assert.True(size > 0, "no size with smeared stem flanks found");

            var snapped = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale((float)size), 0, GlyphMaskMode.Antialiased,
                    GridFit: true, StemSnap: true));
            var unsnapped = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale((float)size), 0, GlyphMaskMode.Antialiased));

            var snappedPartials = CountPartials(snapped, probeRow - snapped.Top);
            var unsnappedPartials = CountPartials(unsnapped, probeRow - unsnapped.Top);

            Assert.True(unsnappedPartials >= 2, $"expected smeared flanks unsnapped, got {unsnappedPartials}");
            Assert.True(snappedPartials == 0,
                $"expected hard stem columns at {size}px, still {snappedPartials} partial cells");
        }

        [Fact]
        public void Curves_And_Diagonals_Are_Untouched()
        {
            var typeface = LoadTypeface();
            var scratch = new GlyphPathBuilder();

            // Diagonal-only glyphs offer no straight vertical flanks; snapping must leave
            // them byte-identical. (Rounds are legitimately adjusted when a design has flat
            // sides — Inter's 'o' does — so they are not asserted here.)
            foreach (var reference in new[] { 'V', 'A' })
            {
                var glyph = typeface.CharacterToGlyphMap[reference];

                var snapped = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(13), 0, GlyphMaskMode.Antialiased,
                        GridFit: true, StemSnap: true));
                var unsnapped = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(13), 0, GlyphMaskMode.Antialiased));

                // The stem-snap variant carries a wider apron; compare ink content at the
                // shared offset — it must be identical, with the extra columns empty.
                var pad = unsnapped.Left - snapped.Left;

                Assert.Equal(unsnapped.Height, snapped.Height);
                Assert.True(pad >= 0);

                for (var y = 0; y < unsnapped.Height; y++)
                {
                    for (var x = 0; x < snapped.Width; x++)
                    {
                        var inner = x - pad;
                        var expected = inner >= 0 && inner < unsnapped.Width
                            ? unsnapped.Alpha[y * unsnapped.Width + inner]
                            : (byte)0;

                        Assert.True(snapped.Alpha[y * snapped.Width + x] == expected,
                            $"'{reference}' ink moved at ({x},{y})");
                    }
                }
            }
        }

        [Fact]
        public void Stems_Render_The_Same_Width_Across_Glyphs()
        {
            var typeface = LoadTypeface();

            // Inter puts lowercase stems at 234-240 design units and capital stems at 248 -
            // a 5% optical correction, far below a pixel at text sizes. Rounding each stem
            // independently splits them into different whole widths at some sizes (29 px:
            // 2.43 rounds to 2 while 2.55 rounds to 3), scattering mixed stem weights across
            // one line. Like the CVT machinery in instructed fonts, close-by widths must
            // unify onto one shared pixel width until the natural difference is big enough
            // to deserve its own.
            var failures = new System.Collections.Generic.List<string>();

            for (var size = 20; size <= 40; size++)
            {
                var lowercase = StemPixels(typeface, 'n', size);
                var capital = StemPixels(typeface, 'H', size);

                if (lowercase > 0 && capital > 0 && lowercase != capital)
                {
                    failures.Add($"{size}px: n stem {lowercase}px, H stem {capital}px");
                }
            }

            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }

        /// <summary>Width in whole pixels of the glyph's left stem under Strong hinting,
        /// measured as the first hard run on a mid-body device row.</summary>
        private static int StemPixels(GlyphTypeface typeface, char reference, float size)
        {
            var glyph = typeface.CharacterToGlyphMap[reference];
            var scratch = new GlyphPathBuilder();
            var mask = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), 0, GlyphMaskMode.Antialiased,
                    GridFit: true, StemSnap: true));

            if (mask.IsEmpty)
            {
                return 0;
            }

            Assert.True(typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap['x'], out var xBox));

            var deviceRow = -(int)Math.Round(xBox.YMax * size / typeface.Metrics.DesignEmHeight / 2);
            var row = deviceRow - mask.Top;

            if (row < 0 || row >= mask.Height)
            {
                return 0;
            }

            var run = 0;

            for (var x = 0; x < mask.Width; x++)
            {
                if (mask.Alpha[row * mask.Width + x] >= 232)
                {
                    run++;
                }
                else if (run > 0)
                {
                    return run;   // the first (left) stem's hard run
                }
            }

            return run;
        }

        private static int CountPartials(GlyphMask mask, int row)
        {
            var partials = 0;

            for (var x = 0; x < mask.Width; x++)
            {
                var coverage = mask.Alpha[row * mask.Width + x];

                if (coverage is > 24 and < 232)
                {
                    partials++;
                }
            }

            return partials;
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
