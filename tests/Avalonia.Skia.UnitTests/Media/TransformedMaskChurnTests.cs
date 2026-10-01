using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A transform that changes every frame (a rotation or zoom animation) must not flood the
    /// shared glyph mask cache with variants that are never drawn again; once the transform
    /// holds still, the run rasterizes and caches as usual.
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
            using var surface = SKSurface.Create(new SKImageInfo(1600, 1600, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            var cache = typeface.MaskCache;

            context.DrawGlyphRun(Brushes.Black, upright);

            var uprightMasks = cache.Count;

            Assert.True(uprightMasks > 0);

            // A hundred and fifty frames at a new angle each: rasterized, their glyph masks
            // would overflow the cache budget.
            for (var frame = 0; frame < 150; frame++)
            {
                context.Transform = Matrix.CreateRotation(Math.PI * (5 + frame * 0.3) / 180) *
                    Matrix.CreateTranslation(300, 300);
                context.DrawGlyphRun(Brushes.Black, animated);
            }

            Assert.True(cache.Evictions == 0,
                $"{cache.Evictions} masks evicted, {cache.Count} cached, {cache.TotalCost / 1024} KB");

            // Only the frames before the guard engaged rasterized.
            var guardedMasks = cache.Count - uprightMasks;

            Assert.True(guardedMasks <= TransformChurnGuard.Threshold * Text.Length,
                $"{guardedMasks} transformed glyph masks entered the cache during the animation");
            Assert.Equal(TransformChurnGuard.Threshold, animated.TransformedSprites.Count);

            // A fresh upright run of the same text composes from the surviving glyph masks.
            using var uprightAgain = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8, 32));
            var before = cache.Count;

            context.Transform = Matrix.Identity;
            context.DrawGlyphRun(Brushes.Black, uprightAgain);
            Assert.Equal(before, cache.Count);

            // The animation stops on a new angle: that frame still counts as animating and
            // rasterizes into transient buffers. The next one repeats the transform,
            // rasterizes and caches, and the one after reuses what it cached.
            var settled = Matrix.CreateRotation(Math.PI * 60 / 180) * Matrix.CreateTranslation(300, 300);

            context.Transform = settled;
            context.DrawGlyphRun(Brushes.Black, animated);

            var masks = cache.Count;

            Assert.Equal(before, masks);

            context.Transform = settled;
            context.DrawGlyphRun(Brushes.Black, animated);

            Assert.True(cache.Count > masks, "the settled draw did not cache its glyph masks");

            masks = cache.Count;

            context.Transform = settled;
            context.DrawGlyphRun(Brushes.Black, animated);

            Assert.Equal(masks, cache.Count);
        }

        [Fact]
        public void Glyph_Masks_Too_Large_For_The_Cache_Budget_Are_Not_Cached()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "HO", 1400, new Point(10, 1300));

            const int width = 2400;
            const int height = 1400;

            // Upright at 1400 px per em, each glyph mask is over a sixteenth of the budget: the
            // run's sprites hold their own masks, and the cache keeps none of them.
            var actual = TransformedCacheBlitTests.RenderOnSurface(width, height, SKColors.Transparent, context =>
            {
                Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, Brushes.Black,
                    TextRenderingMode.Antialias));
            });

            Assert.Equal(0, typeface.MaskCache.Count);
            Assert.True(run.TransformedSprites.TryGet(TransformedAtlasTests.SpriteKey(run, Matrix.Identity), out var sprites));
            Assert.NotNull(sprites.Masks);

            var expected = TransformedCacheBlitTests.ComposeRunMask(typeface, run, Matrix.Identity, Colors.Black,
                new byte[width * height * 4], width, height);

            Assert.True(Array.FindAll(expected, b => b != 0).Length > 100000);
            TransformedGlyphRunTests.AssertEqual(expected, actual, width, 4, "1400 px per em");
        }
    }
}
