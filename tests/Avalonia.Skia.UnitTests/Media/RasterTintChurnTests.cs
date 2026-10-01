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
    /// Upright grayscale text on a CPU surface whose foreground colour changes every frame must
    /// not compose a pre-tinted run mask for every colour. The backend's bitmap blit, which
    /// draws those masks, has its arithmetic pinned here.
    /// </summary>
    public class RasterTintChurnTests
    {
        private const int Width = 300;
        private const int Height = 90;

        private static readonly Color[] s_animation =
        {
            Colors.DarkRed, Colors.DarkGreen, Colors.DarkBlue, Colors.DarkOrange, Colors.Purple,
        };

        /// <summary>
        /// The 1:1 blit of a premultiplied BGRA bitmap, per channel with <c>s</c> the source
        /// channel, <c>sa</c> the source alpha and <c>d</c> the destination channel. Onto a
        /// surface of the bitmap's own byte order the backend copies rows with its sprite
        /// blitter, which scales the destination by <c>256 - sa</c> and shifts it down by 8;
        /// onto any other it converts through its 8-bit raster pipeline, which scales by
        /// <c>255 - sa</c> and divides by 255 as <c>(v + 255) &gt;&gt; 8</c>. Either sum saturates.
        /// </summary>
        internal static byte Blit(byte source, byte sourceAlpha, byte destination, bool sprite)
            => (byte)Math.Min(255, source + (sprite
                ? (destination * (256 - sourceAlpha)) >> 8
                : (destination * (255 - sourceAlpha) + 255) >> 8));

        [Theory]
        [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul, true)]
        [InlineData(SKColorType.Bgra8888, SKAlphaType.Opaque, true)]
        [InlineData(SKColorType.Rgba8888, SKAlphaType.Premul, false)]
        public unsafe void A_Bitmap_Blit_Blends_Every_Channel_By_The_Formula_Of_Its_Blitter(SKColorType colorType,
            SKAlphaType alphaType, bool sprite)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out _);

            // Every source alpha (column) against every destination byte (row); the source
            // channels take the largest and other values a premultiplied pixel allows.
            const int size = 256;
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();

            using var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var framebuffer = bitmap.Lock())
            {
                for (var row = 0; row < size; row++)
                {
                    var pixels = (uint*)((byte*)framebuffer.Address + row * framebuffer.RowBytes);

                    for (var alpha = 0; alpha < size; alpha++)
                    {
                        pixels[alpha] = Source(alpha, row);
                    }
                }
            }

            foreach (var destinationAlpha in alphaType == SKAlphaType.Opaque ? new[] { 255 } : new[] { 255, 200, 64 })
            {
                var info = new SKImageInfo(size, size, colorType, alphaType);

                using var surface = SKSurface.Create(info);

                using (var pixmap = surface.PeekPixels())
                {
                    for (var row = 0; row < size; row++)
                    {
                        var pixels = (uint*)((byte*)pixmap.GetPixels() + row * pixmap.RowBytes);

                        for (var column = 0; column < size; column++)
                        {
                            pixels[column] = Destination(row, destinationAlpha);
                        }
                    }
                }

                using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                       {
                           Surface = surface,
                           Dpi = new Vector(96, 96),
                       }))
                {
                    var rect = new Rect(0, 0, size, size);

                    context.DrawBitmap(bitmap, 1, rect, rect);
                }

                var actual = new uint[size * size];

                fixed (uint* p = actual)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, size * 4, 0, 0));
                }

                for (var row = 0; row < size; row++)
                {
                    var destination = Destination(row, destinationAlpha);

                    for (var alpha = 0; alpha < size; alpha++)
                    {
                        // The bitmap is BGRA; an RGBA surface holds the same channels swapped.
                        var source = Source(alpha, row);

                        if (colorType == SKColorType.Rgba8888)
                        {
                            source = (source & 0xFF00FF00) | ((source >> 16) & 0xFF) | ((source & 0xFF) << 16);
                        }

                        var value = actual[row * size + alpha];

                        for (var shift = 0; shift < 32; shift += 8)
                        {
                            var expected = Blit((byte)(source >> shift), (byte)alpha, (byte)(destination >> shift),
                                sprite);

                            Assert.True(expected == (byte)(value >> shift),
                                $"alpha {alpha}, destination {destination:X8}, byte {shift / 8}: expected {expected}, " +
                                $"actual {(byte)(value >> shift)}");
                        }
                    }
                }
            }

            static uint Source(int alpha, int row)
                => ((uint)alpha << 24) | ((uint)(alpha * row / 255) << 16) | ((uint)(alpha / 3) << 8) | (uint)alpha;

            // A premultiplied destination: the row byte where it fits under the alpha.
            static uint Destination(int row, int alpha)
                => ((uint)alpha << 24) | ((uint)Math.Min(row, alpha) << 16) | ((uint)(Math.Min(row, alpha) / 2) << 8) |
                   (uint)(alpha - Math.Min(row, alpha));
        }

        [Fact]
        public void A_Foreground_Colour_Animation_On_A_Raster_Surface_Allocates_Nothing_Per_Frame()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2));

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            var brushes = Array.ConvertAll(s_animation, color => (IBrush)new ImmutableSolidColorBrush(color));

            // Two cycles let the animation settle into whatever the steady state holds.
            for (var frame = 0; frame < 2 * brushes.Length; frame++)
            {
                context.DrawGlyphRun(brushes[frame % brushes.Length], run);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var frame = 0; frame < 4 * brushes.Length; frame++)
            {
                context.DrawGlyphRun(brushes[frame % brushes.Length], run);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"{4 * brushes.Length} animated frames allocated {allocated} bytes");
        }
    }
}
