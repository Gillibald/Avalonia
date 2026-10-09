using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Static transformed text on a CPU raster surface blends the cached glyph masks straight
    /// into the surface. The pre-tinted run-mask compose is the oracle: the output must
    /// equal it byte for byte, on every instruction-set path, whatever the tint, transform,
    /// overlap or clip.
    /// </summary>
    public class TransformedCacheBlitTests
    {
        private const string Text = "Wavy AVATAR, fjord; 0123";

        public static IEnumerable<object[]> Paths()
        {
            yield return new object[] { nameof(GlyphBlitPath.Scalar) };

            if (Ssse3.IsSupported)
            {
                yield return new object[] { nameof(GlyphBlitPath.Ssse3) };
            }

            if (Avx2.IsSupported)
            {
                yield return new object[] { nameof(GlyphBlitPath.Avx2) };
            }

            if (Vector128.IsHardwareAccelerated)
            {
                yield return new object[] { nameof(GlyphBlitPath.Portable) };
            }

            if (GlyphMaskBlitter.IsSupported(GlyphBlitPath.AdvSimd))
            {
                yield return new object[] { nameof(GlyphBlitPath.AdvSimd) };
            }
        }

        public static IEnumerable<object[]> Scenes()
        {
            var transforms = new (string, Matrix)[]
            {
                ("rotated 17 degrees", Matrix.CreateRotation(Math.PI * 17 / 180) * Matrix.CreateTranslation(40.3, 20.6)),
                ("rotated -40 degrees", Matrix.CreateRotation(-Math.PI * 40 / 180) * Matrix.CreateTranslation(20.7, 260.1)),
                ("skewed", new Matrix(1, 0, -0.35, 1, 30.45, 10.2)),
                ("anisotropic", Matrix.CreateScale(1.8, 0.7) * Matrix.CreateTranslation(10.1, 5.85)),
            };

            foreach (var path in Paths())
            {
                foreach (var (label, transform) in transforms)
                {
                    yield return new object[] { path[0], label, transform };
                }
            }
        }

        private static readonly Color[] s_tints =
        {
            Colors.Black,
            Color.FromArgb(0xFF, 0x20, 0x40, 0x90),
            Color.FromArgb(0xE0, 0xF0, 0xE0, 0x30),
            Color.FromArgb(0x60, 0x10, 0xC0, 0x80),
        };

        [Theory]
        [MemberData(nameof(Paths))]
        public void The_Blend_Equals_The_Compose_Arithmetic_For_Every_Coverage_And_Destination(string path)
        {
            using var restore = UsePath(path);

            // Every (coverage, destination byte) pair, the destination byte repeated into all
            // four channels so each channel sees it; alpha is never below a colour channel in
            // premultiplied data, so equal channels stay valid.
            var alpha = new byte[256 * 256];
            var start = new byte[256 * 256 * 4];

            for (var coverage = 0; coverage < 256; coverage++)
            {
                for (var value = 0; value < 256; value++)
                {
                    var i = coverage * 256 + value;

                    alpha[i] = (byte)coverage;
                    start[i * 4] = start[i * 4 + 1] = start[i * 4 + 2] = start[i * 4 + 3] = (byte)value;
                }
            }

            var mask = new GlyphMask(alpha, 256, 256, 0, 0);

            foreach (var color in s_tints)
            {
                var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);

                foreach (var table in new[] { null, MaskGamma.GetTableForPremulBgra(tint) })
                {
                    var expected = (byte[])start.Clone();

                    RunMaskComposer.ComposeTinted(mask, 0, 0, tint, expected, 256, 256, coverageTable: table);

                    var actual = Blit(start, 256, 256, mask, 0, 0, tint, table, new PixelRect(0, 0, 256, 256));

                    AssertEqual(expected, actual, 256, $"{path} {color} table {table is not null}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void Masks_With_Solid_And_Empty_Spans_Blend_As_The_Compose_At_Every_Width_And_Alignment(string path)
        {
            using var restore = UsePath(path);

            // Rows of every width up to 100 pixels mixing spans of no coverage, full coverage
            // and partial coverage at every alignment, the shape of large glyph masks.
            const int maxWidth = 100;
            var random = new Random(4321);
            var masks = new List<GlyphMask>();

            for (var width = 1; width <= maxWidth; width++)
            {
                const int rows = 3;
                var alpha = new byte[width * rows];

                for (var i = 0; i < alpha.Length;)
                {
                    var span = random.Next(1, 40);
                    var kind = random.Next(4);

                    for (var j = 0; j < span && i < alpha.Length; j++, i++)
                    {
                        alpha[i] = kind switch
                        {
                            0 => 0,
                            1 => 255,
                            2 => (byte)random.Next(256),
                            _ => (byte)(random.Next(2) == 0 ? 255 : random.Next(1, 255)),
                        };
                    }
                }

                masks.Add(new GlyphMask(alpha, width, rows, 0, 0));
            }

            const int surfaceWidth = maxWidth + 20;
            const int surfaceHeight = 4;
            var start = new byte[surfaceWidth * surfaceHeight * 4];

            for (var i = 0; i < start.Length; i += 4)
            {
                var value = (byte)random.Next(256);

                start[i] = start[i + 1] = start[i + 2] = (byte)random.Next(value + 1);
                start[i + 3] = value;
            }

            foreach (var color in s_tints)
            {
                var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);

                foreach (var table in new[] { null, MaskGamma.GetTableForPremulBgra(tint) })
                {
                    foreach (var mask in masks)
                    {
                        var x = mask.Width % 11;
                        var expected = (byte[])start.Clone();

                        RunMaskComposer.ComposeTinted(mask, x, 1, tint, expected, surfaceWidth, surfaceHeight,
                            coverageTable: table);

                        var actual = Blit(start, surfaceWidth, surfaceHeight, mask, x, 1, tint, table,
                            new PixelRect(0, 0, surfaceWidth, surfaceHeight));

                        AssertEqual(expected, actual, surfaceWidth,
                            $"{path} {color} table {table is not null} width {mask.Width}");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void The_Blend_Clips_To_The_Clip_Rectangle_And_The_Surface(string path)
        {
            using var restore = UsePath(path);

            var random = new Random(42);
            const int width = 61;
            const int height = 47;

            for (var trial = 0; trial < 200; trial++)
            {
                var mask = RandomMask(random, random.Next(1, 40), random.Next(1, 40));
                var start = new byte[width * height * 4];

                random.NextBytes(start);
                Premultiply(start);

                var x = random.Next(-30, width);
                var y = random.Next(-30, height);
                var clipX = random.Next(-5, width);
                var clipY = random.Next(-5, height);
                var clip = new PixelRect(clipX, clipY, random.Next(0, width + 10), random.Next(0, height + 10));
                var color = s_tints[trial % s_tints.Length];
                var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                var table = MaskGamma.GetTableForPremulBgra(tint);

                // The compose clips only to its buffer: compose everywhere, then put back what
                // lies outside the clip.
                var expected = (byte[])start.Clone();

                RunMaskComposer.ComposeTinted(mask, x - mask.Left, y - mask.Top, tint, expected, width, height,
                    coverageTable: table);
                RestoreOutside(expected, start, width, height, clip);

                var actual = Blit(start, width, height, mask, x, y, tint, table, clip);

                AssertEqual(expected, actual, width, $"{path} trial {trial}");
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void A_Mask_With_Uncovered_Margins_Blends_As_The_Compose_Of_The_Whole_Mask(string path)
        {
            using var restore = UsePath(path);

            // Transformed glyph masks bound the transformed corners of the ink box, so under
            // rotation their margins hold no coverage; the blend skips them, which must leave
            // the bytes of blending every pixel, whatever the clip cuts off.
            var random = new Random(91);
            const int width = 70;
            const int height = 50;

            for (var trial = 0; trial < 200; trial++)
            {
                var inner = RandomMask(random, random.Next(1, 30), random.Next(1, 30));
                var left = random.Next(0, 9);
                var top = random.Next(0, 9);
                var maskWidth = left + inner.Width + random.Next(0, 9);
                var maskHeight = top + inner.Height + random.Next(0, 9);
                var alpha = new byte[maskWidth * maskHeight];

                for (var row = 0; row < inner.Height; row++)
                {
                    Array.Copy(inner.Alpha, row * inner.Width, alpha, (top + row) * maskWidth + left, inner.Width);
                }

                var mask = new GlyphMask(alpha, maskWidth, maskHeight, 0, 0);
                var start = new byte[width * height * 4];

                random.NextBytes(start);
                Premultiply(start);

                var x = random.Next(-20, width - 5);
                var y = random.Next(-20, height - 5);
                var clip = new PixelRect(random.Next(-5, 20), random.Next(-5, 20), random.Next(10, width + 10),
                    random.Next(10, height + 10));
                var color = s_tints[trial % s_tints.Length];
                var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                var table = trial % 2 == 0 ? MaskGamma.GetTableForPremulBgra(tint) : null;
                var expected = (byte[])start.Clone();

                RunMaskComposer.ComposeTinted(mask, x, y, tint, expected, width, height, coverageTable: table);
                RestoreOutside(expected, start, width, height, clip);

                var actual = Blit(start, width, height, mask, x, y, tint, table, clip);

                AssertEqual(expected, actual, width, $"{path} trial {trial}");
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void An_Rgba_Surface_Gets_The_Same_Blend_With_Red_And_Blue_Swapped(string path)
        {
            using var restore = UsePath(path);

            var random = new Random(5);
            var mask = RandomMask(random, 33, 21);
            var start = new byte[40 * 30 * 4];

            random.NextBytes(start);
            Premultiply(start);

            var color = Color.FromArgb(0xF0, 0x30, 0x80, 0xE0);
            var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
            var table = MaskGamma.GetTableForPremulBgra(tint);
            var expected = (byte[])start.Clone();

            RunMaskComposer.ComposeTinted(mask, 3 - mask.Left, 4 - mask.Top, tint, expected, 40, 30, coverageTable: table);

            var swapped = SwapRedBlue(start);
            var actual = Blit(swapped, 40, 30, mask, 3, 4, tint, table, new PixelRect(0, 0, 40, 30), isRgba: true);

            AssertEqual(expected, SwapRedBlue(actual), 40, $"{path}");
        }

        [Theory]
        [MemberData(nameof(Scenes))]
        public void A_Transformed_Run_On_A_Raster_Surface_Equals_The_Run_Mask_Compose(string path, string label,
            Matrix transform)
        {
            using var restore = UsePath(path);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Squeezed advances make neighbouring glyphs overlap.
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8.37, 32.61), advanceScale: 0.8);

            const int width = 640;
            const int height = 360;

            foreach (var color in s_tints)
            {
                var actual = RenderOnSurface(width, height, SKColors.Transparent, context =>
                {
                    context.Transform = transform;
                    context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run);
                });

                var expected = ComposeRunMask(typeface, run, transform, color, new byte[width * height * 4], width, height);

                AssertEqual(expected, actual, width, $"{path} {label} {color}");
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void A_Run_Crossing_The_Surface_Edges_And_A_Clip_Equals_The_Clipped_Compose(string path)
        {
            using var restore = UsePath(path);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 30, new Point(-12.3, 20.4), advanceScale: 0.85);

            // A small surface that the rotated run leaves on the left, top and right, and a clip
            // rectangle that cuts through glyphs.
            const int width = 200;
            const int height = 90;
            var transform = Matrix.CreateRotation(Math.PI * 12 / 180) * Matrix.CreateTranslation(3.3, -4.2);
            var clip = new PixelRect(17, 9, 150, 61);

            foreach (var color in s_tints)
            {
                var actual = RenderOnSurface(width, height, SKColors.Transparent, context =>
                {
                    context.PushClip(new Rect(clip.X, clip.Y, clip.Width, clip.Height));
                    context.Transform = transform;
                    context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run);
                    context.PopClip();
                });

                // Compose on a margin wide enough for every glyph, then crop and clip.
                const int margin = 200;
                var wide = ComposeRunMask(typeface, run, transform * Matrix.CreateTranslation(margin, margin), color,
                    new byte[(width + 2 * margin) * (height + 2 * margin) * 4], width + 2 * margin, height + 2 * margin);
                var expected = new byte[width * height * 4];

                for (var y = 0; y < height; y++)
                {
                    Array.Copy(wide, ((y + margin) * (width + 2 * margin) + margin) * 4, expected, y * width * 4, width * 4);
                }

                RestoreOutside(expected, new byte[expected.Length], width, height, clip);

                Assert.True(Array.Exists(expected, b => b != 0), "the clip holds no ink");
                AssertEqual(expected, actual, width, $"{path} {color}");
            }
        }

        [Fact]
        public void An_Opaque_Background_Gets_The_Compose_Arithmetic()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            const int width = 480;
            const int height = 240;
            var transform = Matrix.CreateRotation(Math.PI * 15 / 180) * Matrix.CreateTranslation(30.2, 10.7);
            var background = new SKColor(0xF4, 0xE8, 0xD0);

            foreach (var color in s_tints)
            {
                var actual = RenderOnSurface(width, height, background, context =>
                {
                    context.Transform = transform;
                    context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run);
                });

                var start = new byte[width * height * 4];

                for (var i = 0; i < width * height; i++)
                {
                    start[i * 4] = background.Blue;
                    start[i * 4 + 1] = background.Green;
                    start[i * 4 + 2] = background.Red;
                    start[i * 4 + 3] = 255;
                }

                // Every glyph composed onto the background in run order with the compose
                // arithmetic, which is what the blit writes.
                var expected = ComposeRunMask(typeface, run, transform, color, start, width, height);

                AssertEqual(expected, actual, width, $"{color}");
            }
        }

        [Fact]
        public void Raster_Draws_Blit_Into_The_Surface()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8, 32));

            RenderOnSurface(480, 240, SKColors.White, context =>
            {
                context.Transform = s_rotation;
                context.DrawGlyphRun(Brushes.Black, run);
            });

            Assert.True(run.TransformedSprites.TryGet(TransformedAtlasTests.SpriteKey(run, s_rotation), out var sprites));
            Assert.NotNull(sprites.Masks);
            Assert.Null(sprites.FallbackImages);
            Assert.Null(sprites.Batches);
        }

        public static IEnumerable<object[]> Obstacles()
        {
            yield return new object[] { "opacity" };
            yield return new object[] { "layer" };
            yield return new object[] { "rounded clip" };
            yield return new object[] { "canvas without surface" };
        }

        [Theory]
        [MemberData(nameof(Obstacles))]
        public void Draws_The_Surface_Cannot_Take_Directly_Fall_Back_To_Per_Glyph_Bitmaps(string obstacle)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            const int width = 480;
            const int height = 240;

            var actual = obstacle == "canvas without surface"
                ? RenderOnCanvas(width, height, context =>
                {
                    context.Transform = s_rotation;
                    context.DrawGlyphRun(Brushes.Black, run);
                })
                : RenderOnSurface(width, height, SKColors.Transparent, context =>
                {
                    switch (obstacle)
                    {
                        case "opacity":
                            context.PushOpacity(0.5, null);
                            break;
                        case "layer":
                            context.PushLayer(new Rect(0, 0, width, height));
                            break;
                        default:
                            context.PushClip(new RoundedRect(new Rect(0, 0, width, height), 4));
                            break;
                    }

                    context.Transform = s_rotation;
                    context.DrawGlyphRun(Brushes.Black, run);
                    context.Transform = Matrix.Identity;

                    switch (obstacle)
                    {
                        case "opacity":
                            context.PopOpacity();
                            break;
                        case "layer":
                            context.PopLayer();
                            break;
                        default:
                            context.PopClip();
                            break;
                    }
                });

            Assert.True(run.TransformedSprites.TryGet(TransformedAtlasTests.SpriteKey(run, s_rotation), out var sprites));
            Assert.NotNull(sprites.FallbackImages);

            if (obstacle == "opacity")
            {
                return;
            }

            // Without overlaps (normal advances here) the per-glyph bitmaps land the compose
            // pixels exactly; the rounded clip's corners are far from the ink.
            var expected = ComposeRunMask(typeface, run, s_rotation, Colors.Black, new byte[width * height * 4],
                width, height);

            AssertEqual(expected, actual, width, obstacle);
        }

        [Fact]
        public void A_Clip_Pushed_Between_Two_Runs_Clips_The_Second()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            const int width = 480;
            const int height = 240;
            var second = s_rotation * Matrix.CreateTranslation(0, 90);
            var clip = new PixelRect(60, 0, 150, height);

            var actual = RenderOnSurface(width, height, SKColors.Transparent, context =>
            {
                context.Transform = s_rotation;
                context.DrawGlyphRun(Brushes.Black, run);
                context.Transform = Matrix.Identity;
                context.PushClip(new Rect(clip.X, clip.Y, clip.Width, clip.Height));
                context.Transform = second;
                context.DrawGlyphRun(Brushes.Black, run);
                context.Transform = Matrix.Identity;
                context.PopClip();
            });

            var expected = ComposeRunMask(typeface, run, s_rotation, Colors.Black, new byte[width * height * 4],
                width, height);
            var clipped = ComposeRunMask(typeface, run, second, Colors.Black, (byte[])expected.Clone(), width, height);

            RestoreOutside(clipped, expected, width, height, clip);

            AssertEqual(clipped, actual, width, "second run");
        }

        [Fact]
        public void A_Run_Drawn_After_A_Snapshot_And_A_Canvas_Draw_Lands_On_The_Surface()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            const int width = 480;
            const int height = 240;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            surface.Canvas.Clear(SKColors.Transparent);

            SKImage snapshot;
            var second = s_rotation * Matrix.CreateTranslation(0, 90);

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                context.Transform = s_rotation;
                context.DrawGlyphRun(Brushes.Black, run);

                // The snapshot shares the surface's pixels until the next draw through the
                // canvas, which copies them: a run drawn after it must write the new copy.
                snapshot = surface.Snapshot();

                context.Transform = Matrix.Identity;
                context.DrawRectangle(Brushes.Red, null, new RoundedRect(new Rect(470, 230, 5, 5)));
                context.Transform = second;
                context.DrawGlyphRun(Brushes.Black, run);
            }

            using (snapshot)
            {
                var first = ComposeRunMask(typeface, run, s_rotation, Colors.Black, new byte[width * height * 4],
                    width, height);
                var withRectangle = (byte[])first.Clone();

                for (var y = 230; y < 235; y++)
                {
                    for (var x = 470; x < 475; x++)
                    {
                        var i = (y * width + x) * 4;

                        withRectangle[i] = 0;
                        withRectangle[i + 1] = 0;
                        withRectangle[i + 2] = 255;
                        withRectangle[i + 3] = 255;
                    }
                }

                var both = ComposeRunMask(typeface, run, second, Colors.Black, withRectangle, width, height);
                var surfacePixels = new byte[info.BytesSize];
                var snapshotPixels = new byte[info.BytesSize];

                using (var pixmap = surface.PeekPixels())
                {
                    pixmap.GetPixelSpan().CopyTo(surfacePixels);
                }

                using (var pixmap = snapshot.PeekPixels())
                {
                    pixmap.GetPixelSpan().CopyTo(snapshotPixels);
                }

                AssertEqual(first, snapshotPixels, width, "snapshot");
                AssertEqual(both, surfacePixels, width, "surface");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Warm_Raster_Frames_Allocate_Nothing(bool fallback)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8, 32));

            var info = new SKImageInfo(480, 240, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            if (fallback)
            {
                context.PushOpacity(0.5, null);
            }

            context.Transform = s_rotation;
            context.DrawGlyphRun(Brushes.Black, run);
            context.DrawGlyphRun(Brushes.Black, run);

            var before = ThreadAllocations.Start();

            for (var i = 0; i < 100; i++)
            {
                context.DrawGlyphRun(Brushes.Black, run);
            }

            var allocated = ThreadAllocations.Since(before);

            Assert.True(allocated == 0, $"100 warm raster draws allocated {allocated} bytes");
        }

        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 17 / 180) *
            Matrix.CreateTranslation(40.3, 20.6);

        /// <summary>
        /// The pre-tinted run-mask compose: every glyph mask tinted and gamma-corrected
        /// source-over in run order, into <paramref name="destination"/>.
        /// </summary>
        internal static byte[] ComposeRunMask(GlyphTypeface typeface, ManagedGlyphRunImpl run, Matrix transform,
            Color color, byte[] destination, int width, int height)
        {
            var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
            var table = MaskGamma.GetTableForPremulBgra(tint);

            foreach (var (mask, x, y) in TransformedGlyphRunTests.GlyphMasksAtPens(typeface, run, transform))
            {
                RunMaskComposer.ComposeTinted(mask, x, y, tint, destination, width, height, coverageTable: table);
            }

            return destination;
        }

        internal static byte[] RenderOnSurface(int width, int height, SKColor background, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            surface.Canvas.Clear(background);

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                draw(context);
            }

            var pixels = new byte[info.BytesSize];

            using var pixmap = surface.PeekPixels();

            pixmap.GetPixelSpan().CopyTo(pixels);

            return pixels;
        }

        private static byte[] RenderOnCanvas(int width, int height, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.Transparent);

            using (var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            {
                draw(context);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        private static unsafe byte[] Blit(byte[] start, int width, int height, GlyphMask mask, int x, int y, uint tint,
            byte[]? table, PixelRect clip, bool isRgba = false)
        {
            var pixels = (byte[])start.Clone();

            fixed (byte* p = pixels)
            {
                var target = new GlyphBlitTarget((IntPtr)p, width * 4, width, height, clip, isRgba);

                GlyphMaskBlitter.Blend(target, mask, x, y, tint, table);
            }

            return pixels;
        }

        private static GlyphMask RandomMask(Random random, int width, int height)
        {
            var alpha = new byte[width * height];

            for (var i = 0; i < alpha.Length; i++)
            {
                // Runs of empty and full coverage exercise the vector paths' shortcuts.
                alpha[i] = (random.Next(4)) switch
                {
                    0 => 0,
                    1 => 255,
                    _ => (byte)random.Next(256),
                };
            }

            return new GlyphMask(alpha, width, height, -random.Next(0, 3), -random.Next(0, 3));
        }

        private static void Premultiply(byte[] pixels)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];

                pixels[i] = (byte)Math.Min(pixels[i], a);
                pixels[i + 1] = (byte)Math.Min(pixels[i + 1], a);
                pixels[i + 2] = (byte)Math.Min(pixels[i + 2], a);
            }
        }

        private static byte[] SwapRedBlue(byte[] pixels)
        {
            var swapped = (byte[])pixels.Clone();

            for (var i = 0; i < swapped.Length; i += 4)
            {
                (swapped[i], swapped[i + 2]) = (swapped[i + 2], swapped[i]);
            }

            return swapped;
        }

        private static void RestoreOutside(byte[] pixels, byte[] original, int width, int height, PixelRect clip)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (x >= clip.X && x < clip.Right && y >= clip.Y && y < clip.Bottom)
                    {
                        continue;
                    }

                    Array.Copy(original, (y * width + x) * 4, pixels, (y * width + x) * 4, 4);
                }
            }
        }

        private static void AssertEqual(byte[] expected, byte[] actual, int width, string label)
        {
            Assert.Equal(expected.Length, actual.Length);

            var index = actual.AsSpan().CommonPrefixLength(expected);

            if (index != expected.Length)
            {
                var pixel = index / 4;

                Assert.Fail($"{label}: first difference at ({pixel % width}, {pixel / width}) channel {index % 4}: " +
                            $"expected {expected[index]}, actual {actual[index]}");
            }
        }

        private static IDisposable UsePath(string path)
        {
            var previous = GlyphMaskBlitter.Path;

            GlyphMaskBlitter.Path = Enum.Parse<GlyphBlitPath>(path);

            return new RestorePath(previous);
        }

        private sealed class RestorePath : IDisposable
        {
            private readonly GlyphBlitPath _previous;

            public RestorePath(GlyphBlitPath previous) => _previous = previous;

            public void Dispose() => GlyphMaskBlitter.Path = _previous;
        }
    }
}
