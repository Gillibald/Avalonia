using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// An upright zoom gesture on a hardware GPU rasterizes the run at every frame's scale, and
    /// past the upright size limit it does so through the transformed tier.
    /// </summary>
    public class UprightZoomTests
    {
        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Zoom_Gesture_Past_The_Upright_Size_Limit_Draws_Every_Frame(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Zoomed past about 1.7x the glyphs leave the upright tier for the transformed one
            // in the middle of the gesture; their masks, rasterized every frame, then take more
            // rows than the frame's transient page starts with.
            using var run = WideRunMaskTests.CreateRun(typeface, "Zooming paragraph text", 96, new Point(8, 160));

            var info = new SKImageInfo(2000, 500, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            for (var frame = 0; frame < 75; frame++)
            {
                var scale = 1 + frame * 0.0137;

                using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
                {
                    surface!.Canvas.Clear(SKColors.Transparent);
                    context.Transform = Matrix.CreateScale(scale, scale);
                    context.DrawGlyphRun(Brushes.Black, run);
                }

                gpu.GrContext.Flush();

                var pixels = new uint[info.Width * info.Height];

                unsafe
                {
                    fixed (uint* p = pixels)
                    {
                        Assert.True(surface!.ReadPixels(info, (IntPtr)p, info.Width * 4, 0, 0));
                    }
                }

                Assert.True(pixels.Any(pixel => pixel >> 24 != 0), $"frame {frame} at {scale:F3}x drew nothing");
            }
        }
    }
}
