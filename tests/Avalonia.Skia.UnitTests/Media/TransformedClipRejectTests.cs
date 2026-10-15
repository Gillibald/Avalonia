using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A transformed run whose ink lies wholly outside the clip draws nothing, so it lays out no
    /// sprites and rasterizes no mask: a dirty region that leaves most of a grid of rotated cells
    /// outside repaints only the cells inside it, as the backend's own text draw does.
    /// </summary>
    public class TransformedClipRejectTests
    {
        private const int Width = 400;
        private const int Height = 200;

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x30, 0x60));

        public enum Content
        {
            Latin,
            Strike,
        }

        public enum Target
        {
            Raster,
            Metal,
            NativeGl,
            Angle,
            Vulkan,
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                foreach (var content in Enum.GetValues<Content>())
                {
                    yield return new object[] { target, content };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_Transformed_Run_Outside_The_Clip_Lays_Out_And_Rasterizes_Nothing(Target target, Content content)
        {
            using var gpu = target == Target.Raster ? null : ColorAtlasTests.CreateGpu(Backend(target), true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var (run, owner) = CreateRun(content);

            try
            {
                var builds = TextTestApplication.Counters.SpriteSetBuilds;
                var rasterizations = TextTestApplication.Counters.GlyphRasterizations;
                var colorMasks = TextTestApplication.Counters.ColorMaskRasterizations;

                var pixels = Render(gpu, context =>
                {
                    // The clip holds the left half; the turned run sits in the right half.
                    context.PushClip(new Rect(0, 0, Width / 2.0, Height));
                    context.Transform = Matrix.CreateRotation(15 * Math.PI / 180) *
                                        Matrix.CreateTranslation(Width * 0.7, Height / 2.0);
                    context.DrawGlyphRun(s_text, run);
                    context.Transform = Matrix.Identity;
                    context.PopClip();
                });

                Assert.Equal(0, TextTestApplication.Counters.SpriteSetBuilds - builds);
                Assert.Equal(0, TextTestApplication.Counters.GlyphRasterizations - rasterizations);
                Assert.Equal(0, TextTestApplication.Counters.ColorMaskRasterizations - colorMasks);
                Assert.All(pixels, b => Assert.Equal(255, b));
            }
            finally
            {
                run.Dispose();
                GC.KeepAlive(owner);
            }
        }

        /// <summary>
        /// A dirty region of several rectangles, as a frame that repaints a few cells of a grid
        /// clips to: a run in the gap between them lies inside the region's bounds but outside the
        /// region, and lays out nothing either.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void A_Transformed_Run_Between_The_Rectangles_Of_A_Region_Clip_Lays_Out_Nothing(Target target,
            Content content)
        {
            using var gpu = target == Target.Raster ? null : ColorAtlasTests.CreateGpu(Backend(target), true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var (run, owner) = CreateRun(content);
            using var region = new SkiaRegionImpl();

            region.AddRect(new LtrbPixelRect(0, 0, 60, Height));
            region.AddRect(new LtrbPixelRect(Width - 60, 0, Width, Height));

            try
            {
                var builds = TextTestApplication.Counters.SpriteSetBuilds;
                var rasterizations = TextTestApplication.Counters.GlyphRasterizations;
                var colorMasks = TextTestApplication.Counters.ColorMaskRasterizations;

                var pixels = Render(gpu, context =>
                {
                    context.PushClip(region);
                    context.Transform = Matrix.CreateRotation(15 * Math.PI / 180) *
                                        Matrix.CreateTranslation(Width * 0.35, Height / 2.0);
                    context.DrawGlyphRun(s_text, run);
                    context.Transform = Matrix.Identity;
                    context.PopClip();
                });

                Assert.Equal(0, TextTestApplication.Counters.SpriteSetBuilds - builds);
                Assert.Equal(0, TextTestApplication.Counters.GlyphRasterizations - rasterizations);
                Assert.Equal(0, TextTestApplication.Counters.ColorMaskRasterizations - colorMasks);
                Assert.All(pixels, b => Assert.Equal(255, b));
            }
            finally
            {
                run.Dispose();
                GC.KeepAlive(owner);
            }
        }

        private static GpuBackend Backend(Target target) => target switch
        {
            Target.Metal => GpuBackend.Metal,
            Target.NativeGl => GpuBackend.NativeGl,
            Target.Angle => GpuBackend.Angle,
            _ => GpuBackend.Vulkan,
        };

        private static (ManagedGlyphRunImpl Run, GlyphRun? Owner) CreateRun(Content content)
        {
            if (content == Content.Latin)
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

                return (WideRunMaskTests.CreateRun(typeface, "Rotated label", 18, new Point(0, 0)), null);
            }

            var strikes = BackendBitmapStrikeTests.CreateTwoStrikeTypeface(out var glyph);
            var run = new GlyphRun(strikes, 24, default, new List<GlyphInfo> { new(glyph, 0, 28) }, new Point(0, 0));

            return ((ManagedGlyphRunImpl)run.PlatformImpl.Item, run);
        }

        /// <summary>Draws a frame on white and reads it back as BGRA.</summary>
        private static unsafe byte[] Render(GpuTestContext? gpu, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            if (gpu is null)
            {
                using var bitmap = new SKBitmap(info);
                using var canvas = new SKCanvas(bitmap);

                canvas.Clear(SKColors.White);

                using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
                {
                    draw(impl);
                }

                return bitmap.GetPixelSpan().ToArray();
            }

            using var surface = SKSurface.Create(gpu.GrContext, true, info.WithColorType(SKColorType.Rgba8888));

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            surface.Canvas.Clear(SKColors.White);

            using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
            {
                draw(context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[info.BytesSize];

            fixed (byte* p = pixels)
            {
                Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
            }

            return pixels;
        }
    }
}
