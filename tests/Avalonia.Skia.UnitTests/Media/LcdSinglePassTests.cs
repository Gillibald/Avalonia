using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Subpixel text on a CPU display surface: its pixels are those of the portable pair of
    /// payloads drawn with the multiply blend mode and then the plus blend mode, whose 8-bit
    /// arithmetic on Skia's raster pipeline is pinned here per channel.
    /// </summary>
    public class LcdSinglePassTests
    {
        /// <summary>
        /// The two-pass blend of one channel: the multiply blend with an opaque source,
        /// <c>s * (1 - da) + s * d</c>, divided by 255 as <c>(v + 255) &gt;&gt; 8</c>, then the
        /// saturating plus blend.
        /// </summary>
        internal static byte TwoPass(byte multiply, byte plus, byte destination, byte destinationAlpha)
        {
            var multiplied = (multiply * (255 - destinationAlpha) + multiply * destination + 255) >> 8;

            return (byte)Math.Min(255, multiplied + plus);
        }

        [Fact]
        public void The_Two_Pass_Draw_Blends_Every_Channel_By_The_Pipeline_Formula()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out _);

            const int width = 256;

            // Every multiply byte against destination pixels covering opaque and translucent
            // premultiplied values, with plus bytes that saturate and ones that do not.
            var destinations = new uint[]
            {
                0xFF000000, 0xFFFFFFFF, 0xFF808080, 0xFF1F7FDF, 0xFFE03010, 0xFF010203,
                0x80402010, 0x80808080, 0x40000040, 0x00000000, 0xC0B0A090, 0x10101010,
            };
            var height = destinations.Length * 4;

            var multiply = new uint[width * height];
            var plus = new uint[width * height];
            var background = new uint[width * height];

            for (var row = 0; row < height; row++)
            {
                var variant = row % 4;

                for (var column = 0; column < width; column++)
                {
                    var m = (byte)column;
                    var mg = (byte)(255 - column);
                    var mr = (byte)((column * 37) & 0xFF);
                    var coverage = 255 - Math.Min(m, Math.Min(mg, mr));

                    // Plus bytes as the composer makes them (a tint scaled by coverage) and
                    // arbitrary premultiplied ones.
                    var pb = variant switch { 0 => 0, 1 => (255 - m) / 2, 2 => (column * 11) & 0x7F, _ => 255 - m };
                    var pg = variant switch { 0 => 0, 1 => (255 - mg) / 3, 2 => (column * 5) & 0x7F, _ => 255 - mg };
                    var pr = variant switch { 0 => 0, 1 => (255 - mr) / 4, 2 => (column * 3) & 0x7F, _ => 255 - mr };
                    var pa = variant switch { 0 => 0, 3 => coverage, _ => Math.Max(pb, Math.Max(pg, pr)) };

                    multiply[row * width + column] = 0xFF000000u | ((uint)mr << 16) | ((uint)mg << 8) | m;
                    plus[row * width + column] = ((uint)pa << 24) | ((uint)pr << 16) | ((uint)pg << 8) | (uint)pb;
                    background[row * width + column] = destinations[row / 4];
                }
            }

            var actual = DrawTwoPass(multiply, plus, background, width, height);

            for (var i = 0; i < actual.Length; i++)
            {
                var d = background[i];
                var da = (byte)(d >> 24);

                for (var shift = 0; shift < 32; shift += 8)
                {
                    var m = shift == 24 ? (byte)255 : (byte)(multiply[i] >> shift);
                    var expected = TwoPass(m, (byte)(plus[i] >> shift), (byte)(d >> shift), da);
                    var value = (byte)(actual[i] >> shift);

                    Assert.True(expected == value,
                        $"pixel ({i % width}, {i / width}) byte {shift / 8}: expected {expected}, actual {value}" +
                        $" (m {m}, p {(byte)(plus[i] >> shift)}, d {(byte)(d >> shift)}, da {da})");
                }
            }
        }

        [Fact]
        public void The_Pipeline_Multiply_Stays_Within_One_Level_Of_The_Rounded_Product()
        {
            var below = 0;
            var above = 0;

            for (var m = 0; m < 256; m++)
            {
                for (var factor = 0; factor < 256; factor++)
                {
                    // With da = 255 the factor is the destination channel; otherwise 255 - da + d.
                    var pipeline = (m * factor + 255) >> 8;
                    var rounded = (m * factor + 127) / 255;

                    Assert.InRange(pipeline - rounded, -1, 1);

                    below += pipeline < rounded ? 1 : 0;
                    above += pipeline > rounded ? 1 : 0;
                }
            }

            // Both directions occur, so the bound is tight.
            Assert.True(below > 0 && above > 0);
        }

        [Theory]
        [InlineData(SKColorType.Bgra8888, SKPixelGeometry.RgbHorizontal)]
        [InlineData(SKColorType.Bgra8888, SKPixelGeometry.BgrHorizontal)]
        [InlineData(SKColorType.Rgba8888, SKPixelGeometry.RgbHorizontal)]
        public void Subpixel_Text_Draws_The_Pixels_Of_Its_Two_Pass_Payloads(SKColorType colorType,
            SKPixelGeometry geometry)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var runs = new[]
            {
                WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(6.37, 20.2)),
                WideRunMaskTests.CreateRun(typeface, "The quick brown fox jumps", 11, new Point(3.71, 42.6)),
                WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 23, new Point(9.12, 74.3)),
            };

            var brushes = new IBrush[]
            {
                Brushes.Black,
                new ImmutableSolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x10)),
                new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x60, 0xE0)),
            };

            try
            {
                var direct = Render(colorType, geometry, runs, brushes, directSurfaceWrites: true);
                var portable = Render(colorType, geometry, runs, brushes, directSurfaceWrites: false);

                for (var i = 0; i < direct.Length; i++)
                {
                    Assert.True(direct[i] == portable[i],
                        $"byte {i} (pixel {i / 4 % 260}, {i / 4 / 260}): direct {direct[i]}, two-pass {portable[i]}");
                }
            }
            finally
            {
                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }

        private static byte[] Render(SKColorType colorType, SKPixelGeometry geometry, ManagedGlyphRunImpl[] runs,
            IBrush[] brushes, bool directSurfaceWrites)
        {
            var info = new SKImageInfo(260, 96, colorType, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(geometry));

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                context.AllowsDirectSurfaceWrites = directSurfaceWrites;

                // A backdrop with opaque colours, a gradient and a translucent band, which the
                // multiply blend also changes where coverage is zero.
                surface.Canvas.Clear(new SKColor(0xF0, 0xE8, 0xD0));

                using (var gradient = new SKPaint())
                {
                    gradient.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(260, 0),
                        new[] { SKColors.White, new SKColor(0x20, 0x40, 0x80) }, SKShaderTileMode.Clamp);
                    surface.Canvas.DrawRect(0, 28, 260, 22, gradient);
                }

                using (var band = new SKPaint { Color = new SKColor(0, 0, 0, 0), BlendMode = SKBlendMode.Src })
                {
                    surface.Canvas.DrawRect(120, 0, 40, 96, band);
                }

                using (var translucent = new SKPaint { Color = new SKColor(0x30, 0x60, 0x90, 0x80), BlendMode = SKBlendMode.Src })
                {
                    surface.Canvas.DrawRect(160, 0, 50, 96, translucent);
                }

                context.PushTextOptions(new TextOptions { TextRenderingMode = TextRenderingMode.SubpixelAntialias });

                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(brushes[i], runs[i]);
                }

                context.PopTextOptions();
            }

            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }
            }

            return pixels;
        }

        /// <summary>
        /// Draws the payloads over the background as the portable subpixel draw does: the
        /// multiply payload with the multiply blend mode, then the plus payload with plus.
        /// </summary>
        private static unsafe uint[] DrawTwoPass(uint[] multiply, uint[] plus, uint[] background, int width, int height)
        {
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var multiplyBitmap = CreateBitmap(renderInterface, multiply, width, height);
            using var plusBitmap = CreateBitmap(renderInterface, plus, width, height);

            using (var pixmap = surface.PeekPixels())
            {
                for (var row = 0; row < height; row++)
                {
                    new ReadOnlySpan<uint>(background, row * width, width).CopyTo(
                        new Span<uint>((byte*)pixmap.GetPixels() + row * pixmap.RowBytes, width));
                }
            }

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                var bounds = new Rect(0, 0, width, height);

                context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Multiply });
                context.DrawBitmap((IBitmapImpl)multiplyBitmap, 1, bounds, bounds);
                context.PopRenderOptions();

                context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Plus });
                context.DrawBitmap((IBitmapImpl)plusBitmap, 1, bounds, bounds);
                context.PopRenderOptions();
            }

            var result = new uint[width * height];

            fixed (uint* p = result)
            {
                Assert.True(surface.ReadPixels(info, (IntPtr)p, width * 4, 0, 0));
            }

            return result;
        }

        private static unsafe IWriteableBitmapImpl CreateBitmap(IPlatformRenderInterface renderInterface, uint[] pixels,
            int width, int height)
        {
            var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            using var framebuffer = bitmap.Lock();

            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<uint>(pixels, row * width, width)
                    .CopyTo(new Span<uint>((byte*)framebuffer.Address + row * framebuffer.RowBytes, width));
            }

            return bitmap;
        }
    }
}
