using System;
using System.Collections.Generic;
using System.Linq;
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
        public void A_Zoom_Gesture_Keeps_No_Glyph_Masks_And_Allocates_Independently_Of_Text_Size(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var small = Zoom(gpu, typeface, 24, out var smallMasksAdded);
            var large = Zoom(gpu, typeface, 96, out var largeMasksAdded);

            Assert.Equal(0, smallMasksAdded);
            Assert.Equal(0, largeMasksAdded);

            // Sixteen times the pixel area: the glyph masks of the large run alone are hundreds of
            // kilobytes a frame, while what a frame allocates beyond them does not depend on size.
            Assert.True(Math.Abs(large - small) < 512,
                $"a zoom frame allocated {small} bytes at 24 px and {large} bytes at 96 px");
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

        /// <summary>
        /// Zooms a run until the gesture rasterizes every frame, then returns what a frame
        /// allocates and how many glyph masks the typeface's cache gained meanwhile.
        /// </summary>
        private static long Zoom(GpuTestContext gpu, Avalonia.Media.GlyphTypeface typeface, double emSize,
            out int masksAdded)
        {
            using var run = WideRunMaskTests.CreateRun(typeface, "Zooming paragraph text", emSize, new Point(8, 120));

            var info = new SKImageInfo(1800, 300, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            void Frame(int frame)
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);
                var scale = 1 + frame * 0.0137;

                context.Transform = Matrix.CreateScale(scale, scale);
                context.DrawGlyphRun(Brushes.Black, run);
            }

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

            masksAdded = typeface.MaskCache.Count - masksBefore;

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 30;
        }
    }
}
