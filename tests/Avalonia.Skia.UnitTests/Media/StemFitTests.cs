using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Horizontal stem snapping of the auto-hinter under bi-level Strong hinting, the one mode
    /// that fits x on whole-pixel pens: close stem widths unify onto one pixel width, while
    /// curves and diagonals, where snapping would distort, stay byte-identical to the
    /// unsnapped build. Grayscale and subpixel Strong leave x natural.
    /// </summary>
    public class StemFitTests
    {
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
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(13), 0, GlyphMaskMode.Aliased,
                        GridFit: true, Strong: true));
                var unsnapped = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(13), 0, GlyphMaskMode.Aliased));

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

        /// <summary>Width in whole pixels of the glyph's left stem under bi-level Strong hinting,
        /// measured as the first hard run on a mid-body device row.</summary>
        private static int StemPixels(GlyphTypeface typeface, char reference, float size)
        {
            var glyph = typeface.CharacterToGlyphMap[reference];
            var scratch = new GlyphPathBuilder();
            var mask = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), 0, GlyphMaskMode.Aliased,
                    GridFit: true, Strong: true));

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

        [Fact]
        public void Widths_Within_The_CutIn_Snap_To_The_Standard()
        {
            // Two synthetic stems, 1.4 and 1.55 px wide: with a 1.5 px (150-unit) standard
            // both must render 2 px wide; with the standard out of reach past the cut-in,
            // independent rounding splits them 1 vs 2.
            var contours = new GlyphPathBuilder();

            AddRectangle(contours, 2.2f, 3.6f, 0, 10);
            AddRectangle(contours, 8.1f, 9.65f, 0, 10);

            var unified = StemFit.BuildWarp(contours, 1f, new[] { 150f }, designToPixels: 0.01f);
            var natural = StemFit.BuildWarp(contours, 1f, new[] { 500f }, designToPixels: 0.01f);

            Assert.False(unified.IsIdentity);
            Assert.False(natural.IsIdentity);

            Assert.Equal(2f, unified.To[1] - unified.To[0], 3);
            Assert.Equal(2f, unified.To[3] - unified.To[2], 3);

            Assert.Equal(1f, natural.To[1] - natural.To[0], 3);
            Assert.Equal(2f, natural.To[3] - natural.To[2], 3);
        }

        private static void AddRectangle(GlyphPathBuilder contours, float left, float right,
            float top, float bottom)
        {
            contours.BeginFigure(new Avalonia.Point(left, top), true);
            contours.LineTo(new Avalonia.Point(left, bottom));
            contours.LineTo(new Avalonia.Point(right, bottom));
            contours.LineTo(new Avalonia.Point(right, top));
            contours.EndFigure(true);
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

            return TestGlyphTypefaces.FromSKTypeface(skTypeface!);
        }
    }
}
