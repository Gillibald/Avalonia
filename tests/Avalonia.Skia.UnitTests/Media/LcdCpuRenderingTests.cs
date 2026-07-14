using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The portable two-pass subpixel draw on CPU raster targets: fringes with the physical
    /// polarity on eligible surfaces, grayscale purity for Antialias, and the vetoes (layers,
    /// tracked ambient opacity) degrading rather than blending wrong. Runs everywhere — no GPU.
    /// </summary>
    public class LcdCpuRenderingTests
    {
        [Fact]
        public void Cpu_Text_Fringes_With_The_Right_Polarity_And_Grayscale_Stays_Pure()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "HHH", 24);

            var lcdFringes = RenderAndCountFringes(surface, info, run, TextRenderingMode.SubpixelAntialias,
                out var warmLeft, out var coolRight, insideLayer: false, ambientOpacity: null);

            Assert.True(lcdFringes > 20, $"expected fringing edge pixels, found {lcdFringes}");
            Assert.True(warmLeft > 0, "no warm left-edge pixels — stripe order looks swapped");
            Assert.True(coolRight > 0, "no cool right-edge pixels — stripe order looks swapped");

            var grayFringes = RenderAndCountFringes(surface, info, run, TextRenderingMode.Antialias,
                out _, out _, insideLayer: false, ambientOpacity: null);

            Assert.Equal(0, grayFringes);
        }

        [Fact]
        public void Unspecified_Defaults_To_Subpixel_On_An_Eligible_Surface()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "HHH", 24);

            var fringes = RenderAndCountFringes(surface, info, run, TextRenderingMode.Unspecified,
                out _, out _, insideLayer: false, ambientOpacity: null);

            Assert.True(fringes > 20, $"Unspecified must render subpixel here, found {fringes} fringed pixels");
        }

        [Fact]
        public void Layers_And_Tracked_Opacity_Degrade_To_Grayscale()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "HHH", 24);

            var inLayer = RenderAndCountFringes(surface, info, run, TextRenderingMode.SubpixelAntialias,
                out _, out _, insideLayer: true, ambientOpacity: null);

            Assert.Equal(0, inLayer);

            var underOpacity = RenderAndCountFringes(surface, info, run, TextRenderingMode.SubpixelAntialias,
                out _, out _, insideLayer: false, ambientOpacity: 0.5);

            Assert.Equal(0, underOpacity);
        }

        [Fact]
        public void The_Two_Passes_Compose_To_The_Formula_On_A_Colored_Background()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "H", 32);

            var background = new SKColor(0x40, 0x80, 0xC0);
            var foreground = new ImmutableSolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x10));

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(background);
                context.PushRenderOptions(new RenderOptions { TextRenderingMode = TextRenderingMode.SubpixelAntialias });
                context.DrawGlyphRun(foreground, run);
                context.PopRenderOptions();
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            // Every pixel must satisfy the per-channel lerp for SOME coverage triple: each
            // channel lies between the background and the tint, and rounding stays within the
            // two-pass quantization. A double-multiplied or double-added pass breaks this.
            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    var pixel = readback.GetPixel(x, y);

                    AssertChannelInRange(pixel.Red, 0xCC, background.Red, x, y);
                    AssertChannelInRange(pixel.Green, 0x20, background.Green, x, y);
                    AssertChannelInRange(pixel.Blue, 0x10, background.Blue, x, y);
                }
            }
        }

        [Fact]
        public void Strong_Hinting_Renders_Identically_At_Fractional_Origins()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();

            // Two origins inside the same pixel: Strong must produce byte-identical output
            // (every pen rounds), while the default quarter-phase positioning renders them
            // differently — that per-instance variation is what reads as softness.
            var first = RenderAt(surface, info, typeface, 8.26, TextHintingMode.Strong);
            var second = RenderAt(surface, info, typeface, 8.49, TextHintingMode.Strong);

            Assert.True(first.AsSpan().SequenceEqual(second), "Strong output varies with subpixel origin");

            var lightFirst = RenderAt(surface, info, typeface, 8.26, TextHintingMode.Light);
            var lightSecond = RenderAt(surface, info, typeface, 8.49, TextHintingMode.Light);

            Assert.False(lightFirst.AsSpan().SequenceEqual(lightSecond),
                "expected subpixel positioning to differ between phases under Light");
        }

        private byte[] RenderAt(SKSurface surface, SKImageInfo info, GlyphTypeface typeface,
            double originX, TextHintingMode hinting)
        {
            using var run = CreateRunAt(typeface, "HHH", 24, originX);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushTextOptions(new TextOptions
                {
                    TextRenderingMode = TextRenderingMode.SubpixelAntialias,
                    TextHintingMode = hinting,
                });
                context.DrawGlyphRun(Brushes.Black, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var bytes = new byte[info.Width * info.Height * 4];

            System.Runtime.InteropServices.Marshal.Copy(readback.GetPixels(), bytes, 0, bytes.Length);

            return bytes;
        }

        private static ManagedGlyphRunImpl CreateRunAt(GlyphTypeface typeface, string text,
            double emSize, double originX)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new Avalonia.Skia.SkiaManagedGlyphRunImpl(typeface, emSize, infos, new Point(originX, 34));
        }

        private static void AssertChannelInRange(byte value, byte tint, byte background, int x, int y)
        {
            var low = Math.Min(tint, background) - 2;
            var high = Math.Max(tint, background) + 2;

            Assert.True(value >= low && value <= high,
                $"({x},{y}): channel {value} outside [{low},{high}] for tint {tint} over {background}");
        }

        private static int RenderAndCountFringes(SKSurface surface, SKImageInfo info, ManagedGlyphRunImpl run,
            TextRenderingMode mode, out int warmLeft, out int coolRight, bool insideLayer, double? ambientOpacity)
        {
            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushRenderOptions(new RenderOptions { TextRenderingMode = mode });

                if (insideLayer)
                {
                    context.PushLayer(new Rect(0, 0, info.Width, info.Height));
                }

                if (ambientOpacity is { } opacity)
                {
                    context.PushOpacity(opacity, null);
                }

                context.DrawGlyphRun(Brushes.Black, run);

                if (ambientOpacity is not null)
                {
                    context.PopOpacity();
                }

                if (insideLayer)
                {
                    context.PopLayer();
                }

                context.PopRenderOptions();
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var fringes = 0;

            warmLeft = 0;
            coolRight = 0;

            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 1; x < info.Width - 1; x++)
                {
                    var pixel = readback.GetPixel(x, y);

                    if (Math.Abs(pixel.Red - pixel.Blue) <= 8)
                    {
                        continue;
                    }

                    fringes++;

                    var leftNeighborInk = Luma(readback.GetPixel(x - 1, y)) < Luma(pixel);
                    var rightNeighborInk = Luma(readback.GetPixel(x + 1, y)) < Luma(pixel);

                    if (pixel.Red > pixel.Blue && rightNeighborInk && !leftNeighborInk)
                    {
                        warmLeft++;
                    }

                    if (pixel.Blue > pixel.Red && leftNeighborInk && !rightNeighborInk)
                    {
                        coolRight++;
                    }
                }
            }

            return fringes;
        }

        private static int Luma(SKColor color) => 54 * color.Red + 183 * color.Green + 19 * color.Blue;

        private static IDisposable StartApp()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null)));

        private static SKSurface CreateSurface(out SKImageInfo info)
        {
            info = new SKImageInfo(120, 48, SKColorType.Bgra8888, SKAlphaType.Premul);

            return SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        }

        private static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface, string text, double emSize)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new Avalonia.Skia.SkiaManagedGlyphRunImpl(typeface, emSize, infos, new Point(8, 34));
        }

        private static GlyphTypeface LoadTypeface()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf"));
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
        }
    }
}
