using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A transform that changes every frame (a rotation or zoom animation) must not flood the
    /// shared glyph mask cache or the run's mask cache with variants that are never drawn
    /// again; once the transform holds still, the run caches as usual.
    /// </summary>
    public class TransformedMaskChurnTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";

        [Fact]
        public void An_Animated_Rotation_Does_Not_Evict_Upright_Masks_And_Settling_Caches_Again()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var upright = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8, 32));
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 64, new Point(8, 80));

            var context = new TransformedRunMaskTests.DeviceMaskContext(1600, 1600, int.MaxValue);
            var cache = typeface.MaskCache;

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, upright, Brushes.Black, TextRenderingMode.Antialias));

            var uprightMasks = cache.Count;
            var uprightRunMasks = context.Created;

            Assert.True(uprightMasks > 0);

            // A hundred and fifty frames at a new angle each: unguarded, their glyph masks
            // would overflow the cache budget.
            for (var frame = 0; frame < 150; frame++)
            {
                context.Transform = Matrix.CreateRotation(Math.PI * (5 + frame * 0.3) / 180) *
                    Matrix.CreateTranslation(300, 300);

                Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, animated, Brushes.Black,
                    TextRenderingMode.Antialias));
            }

            Assert.True(cache.Evictions == 0,
                $"{cache.Evictions} masks evicted, {cache.Count} cached, {cache.TotalCost / 1024} KB");

            // Only the frames before the guard engaged entered the caches, and every mask of an
            // uncached frame was released after its draw.
            var guardedMasks = cache.Count - uprightMasks;

            Assert.True(guardedMasks <= TransformChurnGuard.Threshold * Text.Length,
                $"{guardedMasks} transformed glyph masks entered the cache during the animation");
            Assert.Equal(TransformChurnGuard.Threshold, context.Created - context.Disposed - uprightRunMasks);

            // A fresh upright run of the same text composes from the surviving glyph masks.
            using var uprightAgain = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8, 32));
            var before = cache.Count;

            context.Transform = Matrix.Identity;
            Assert.True(MaskGlyphRunRenderer.TryDraw(context, uprightAgain, Brushes.Black, TextRenderingMode.Antialias));
            Assert.Equal(before, cache.Count);

            // The last animation frame lands on a new angle and still draws uncached. Settle:
            // the first draw that repeats the transform caches the run mask and its glyph
            // masks, and the next draw reuses them.
            var settled = Matrix.CreateRotation(Math.PI * 60 / 180) * Matrix.CreateTranslation(300, 300);

            context.Transform = settled;
            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, animated, Brushes.Black, TextRenderingMode.Antialias));

            var created = context.Created;
            var released = context.Disposed;
            var masks = cache.Count;

            context.Transform = settled;
            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, animated, Brushes.Black, TextRenderingMode.Antialias));

            Assert.Equal(created + 1, context.Created);
            Assert.Equal(released, context.Disposed);
            Assert.True(cache.Count > masks, "the settled draw did not cache its glyph masks");

            context.Transform = settled;
            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, animated, Brushes.Black, TextRenderingMode.Antialias));

            Assert.Equal(created + 1, context.Created);
            Assert.Equal(released, context.Disposed);
        }

        [Fact]
        public void Glyph_Masks_Too_Large_For_The_Cache_Budget_Are_Not_Cached()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "HO", 1400, new Point(10, 1300));

            var context = new TransformedRunMaskTests.DeviceMaskContext(2400, 1400, int.MaxValue);
            var cache = typeface.MaskCache;

            // Upright at 1400 px per em, each glyph mask is over a sixteenth of the budget.
            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, Brushes.Black, TextRenderingMode.Antialias));

            Assert.Equal(0, cache.Count);
            Assert.True(context.Created > 0);
            Assert.Equal(0, context.Disposed);

            var expected = TransformedRunMaskTests.ComposeExpected(typeface, run, Matrix.Identity, 2400, 1400, out var inked);

            Assert.True(inked > 100000);
            TransformedRunMaskTests.AssertEqual(expected, context.Canvas, 2400, 1, "1400 px per em");
        }
    }
}
