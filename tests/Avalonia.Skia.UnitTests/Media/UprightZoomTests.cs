using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// An upright zoom gesture on a hardware GPU rasterizes the run at every frame's scale. Its
    /// glyph masks are drawn once and never again, so they must not fill the typeface's glyph
    /// mask cache, and a frame must not allocate in proportion to the text's pixel area.
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
        public void A_Zoom_Gesture_Keeps_No_Glyph_Masks_And_Allocates_Little_Per_Frame(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Zooming paragraph text", 48, new Point(8, 60));

            var info = new SKImageInfo(900, 200, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            void Frame(int frame)
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);
                var scale = 1 + frame * 0.0137;

                context.Transform = Matrix.CreateScale(scale, scale);
                context.DrawGlyphRun(Brushes.Black, run);
            }

            // The gesture settles into rasterizing every frame after a few distinct scales.
            for (var frame = 0; frame < 10; frame++)
            {
                Frame(frame);
            }

            var masksBefore = typeface.MaskCache.Count;
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var frame = 10; frame < 40; frame++)
            {
                Frame(frame);
            }

            var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 30;

            Assert.Equal(masksBefore, typeface.MaskCache.Count);

            // The run mask's image and its wrapper are made per frame; the glyph masks of a
            // 48 px run alone are tens of kilobytes.
            Assert.True(perFrame < 4096, $"a zoom frame allocated {perFrame} bytes");
        }
    }
}
