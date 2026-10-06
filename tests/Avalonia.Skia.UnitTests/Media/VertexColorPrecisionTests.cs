using System;
using System.Collections.Generic;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A GPU context registered as drawing translucent per-vertex colours exactly draws an A8
    /// coverage ramp modulated by such a colour with the bytes of the same ramp drawn under that
    /// colour as the paint colour, which is what lets glyph batches fold translucent runs.
    /// </summary>
    public class VertexColorPrecisionTests
    {
        public static IEnumerable<object[]> Backends()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
            yield return new object[] { GpuBackend.Vulkan };
        }

        private static readonly SKColor[] s_backgrounds =
        {
            SKColors.Transparent, SKColors.White, new SKColor(0x30, 0x80, 0xC0), new SKColor(0x80, 0x40, 0x20, 0x80),
        };

        [Theory]
        [MemberData(nameof(Backends))]
        public void A_Context_Registered_As_Exact_Draws_Translucent_Vertex_Colours_As_Paint_Colours(GpuBackend backend)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");
            Assert.SkipUnless(SkiaVertexColorPrecision.IsExact(gpu!.GrContext),
                $"{backend} is not registered as drawing translucent vertex colours exactly");

            var colours = TranslucentColours();
            var expected = Render(gpu, colours, perVertex: false);
            var actual = Render(gpu, colours, perVertex: true);

            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    var row = i / 4 / 256;

                    Assert.Fail($"colour {colours[row / s_backgrounds.Length]} over " +
                                $"{s_backgrounds[row % s_backgrounds.Length]}, coverage {i / 4 % 256}, " +
                                $"channel {i % 4}: {actual[i]} instead of {expected[i]}");
                }
            }
        }

        [Fact]
        public void Desktop_Gl_Draws_Translucent_Vertex_Colours_Exactly()
        {
            using var gpu = GpuTestContext.TryCreate(GpuBackend.NativeGl, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable NativeGl context: {reason}");

            // Desktop GLSL has no reduced precision, so Skia's half is a float there.
            Assert.True(SkiaVertexColorPrecision.IsExact(gpu!.GrContext));
        }

        [Fact]
        public void A_Context_Nobody_Registered_Does_Not_Fold_Translucent_Vertex_Colours()
        {
            using var gpu = GpuTestContext.TryCreate(GpuBackend.Vulkan, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable Vulkan context: {reason}");
            Assert.False(SkiaVertexColorPrecision.IsExact(gpu!.GrContext));
        }

        private static List<SKColor> TranslucentColours()
        {
            var random = new Random(1234);
            var alphas = new byte[] { 0x01, 0x40, 0x7F, 0x80, 0xA0, 0xC0, 0xFE };
            var colours = new List<SKColor>
            {
                new(0x10, 0x10, 0x10, 0xA0), new(0xD0, 0x20, 0x10, 0xC0), new(0x00, 0x00, 0xC0, 0x80),
                new(0x1F, 0x1F, 0x1F, 0x7F), new(0x60, 0x10, 0x30, 0xB0),
            };

            for (var i = 0; i < 60; i++)
            {
                colours.Add(new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256),
                    alphas[i % alphas.Length]));
            }

            return colours;
        }

        // One row per colour and background: the coverage ramp 0-255 drawn 1:1 with nearest
        // sampling, through a quad modulated by the colour per vertex or under a paint colour.
        private static byte[] Render(GpuTestContext gpu, List<SKColor> colours, bool perVertex)
        {
            var rows = colours.Count * s_backgrounds.Length;
            var info = new SKImageInfo(256, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
            var coverage = new byte[256];

            for (var i = 0; i < coverage.Length; i++)
            {
                coverage[i] = (byte)i;
            }

            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

            using var ramp = SKImage.FromPixelCopy(new SKImageInfo(256, 1, SKColorType.Alpha8, SKAlphaType.Premul),
                coverage, 256);
            using var shader = ramp.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);

            for (var row = 0; row < rows; row++)
            {
                var colour = colours[row / s_backgrounds.Length];

                using (var background = new SKPaint { Color = s_backgrounds[row % s_backgrounds.Length], BlendMode = SKBlendMode.Src })
                {
                    canvas.DrawRect(SKRect.Create(0, row, 256, 1), background);
                }

                using var paint = new SKPaint { Color = perVertex ? SKColors.White : colour, Shader = shader };
                using var quad = Quad(row, perVertex ? colour : null);

                canvas.DrawVertices(quad, SKBlendMode.Modulate, paint);
            }

            gpu.GrContext.Flush();

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

        private static SKVertices Quad(int y, SKColor? colour)
        {
            var positions = new[] { new SKPoint(0, y), new SKPoint(256, y), new SKPoint(0, y + 1), new SKPoint(256, y + 1) };
            var texture = new[] { new SKPoint(0, 0), new SKPoint(256, 0), new SKPoint(0, 1), new SKPoint(256, 1) };
            var indices = new ushort[] { 0, 1, 2, 1, 3, 2 };
            var colors = colour is { } c ? new[] { c, c, c, c } : null;

            return SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texture, colors, indices);
        }
    }
}
