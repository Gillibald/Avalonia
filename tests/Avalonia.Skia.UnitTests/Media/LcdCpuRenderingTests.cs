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
        public void Offscreen_Surfaces_Keep_Unspecified_Text_Grayscale_And_Clean_On_Transparency()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "HHH", 24);

            // The render-target-bitmap shape: an offscreen surface that declares RGB stripe
            // geometry (SurfaceRenderTarget does, for every offscreen bitmap) but whose output
            // is captured, composed or read back with alpha rather than presented on a panel.
            // Unspecified must resolve to grayscale here — per-channel coverage has no valid
            // meaning over a transparent destination, and the backend blob renders grayscale
            // on the same surface.
            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.Transparent);
                context.DrawGlyphRun(Brushes.Black, run);
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var chroma = 0;
            var lightOpaque = 0;

            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    var pixel = readback.GetPixel(x, y);

                    if (Math.Abs(pixel.Red - pixel.Blue) > 8 || Math.Abs(pixel.Red - pixel.Green) > 8)
                    {
                        chroma++;
                    }

                    if (pixel.Alpha > 200 && pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200)
                    {
                        lightOpaque++;
                    }
                }
            }

            Assert.Equal(0, chroma);
            Assert.Equal(0, lightOpaque);
        }

        [Fact]
        public void Render_Target_Bitmaps_Keep_Text_Grayscale_And_Clean_On_Transparency()
        {
            using var app = StartApp();

            var typeface = LoadTypeface();
            using var run = CreateRun(typeface, "HHH", 24);

            // The full RenderTargetBitmap path: its framebuffer target must classify as a
            // readback surface, not a display, or subpixel text engages and composes white
            // pillows onto the transparent background (the render suite's immediate images
            // showed exactly that while the marked compositor surface stayed clean).
            using var bitmap = new Avalonia.Skia.RenderTargetBitmapImpl(new PixelSize(160, 48), new Vector(96, 96));

            using (var context = bitmap.CreateDrawingContext())
            {
                context.Clear(Colors.Transparent);
                context.DrawGlyphRun(Brushes.Black, run);
            }

            var chroma = 0;
            var lightOpaque = 0;

            using (var framebuffer = bitmap.Lock())
            {
                var isRgba = framebuffer.Format == PixelFormat.Rgba8888;
                var pixels = new byte[framebuffer.RowBytes * framebuffer.Size.Height];

                System.Runtime.InteropServices.Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

                for (var y = 0; y < framebuffer.Size.Height; y++)
                {
                    var row = y * framebuffer.RowBytes;

                    for (var x = 0; x < framebuffer.Size.Width; x++)
                    {
                        var b = pixels[row + x * 4 + (isRgba ? 2 : 0)];
                        var g = pixels[row + x * 4 + 1];
                        var r = pixels[row + x * 4 + (isRgba ? 0 : 2)];
                        var a = pixels[row + x * 4 + 3];

                        if (Math.Abs(r - b) > 8 || Math.Abs(r - g) > 8)
                        {
                            chroma++;
                        }

                        if (a > 200 && r > 200 && g > 200 && b > 200)
                        {
                            lightOpaque++;
                        }
                    }
                }
            }

            Assert.Equal(0, chroma);
            Assert.Equal(0, lightOpaque);
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
                       SurfaceIsDisplay = true,
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

        [Fact]
        public void Unspecified_Hinting_Renders_As_Light_On_The_Native_Blob()
        {
            using var app = StartApp();
            using var surface = CreateSurface(out var info);
            var typeface = LoadTypeface();

            // DirectWrite has no strong-hinting mode of its own - its native rendering is
            // light-class at every size, with full grid-fitting only behind GDI-compat
            // requests. The blob's Unspecified default must therefore be Light, matching
            // both the platform and the managed path's Unspecified resolution.
            var unspecified = RenderBlobAt(surface, info, typeface, TextHintingMode.Unspecified);
            var light = RenderBlobAt(surface, info, typeface, TextHintingMode.Light);

            Assert.True(unspecified.AsSpan().SequenceEqual(light),
                "Unspecified must render identically to Light on the native blob");
        }

        private byte[] RenderBlobAt(SKSurface surface, SKImageInfo info, GlyphTypeface typeface,
            TextHintingMode hinting)
        {
            using var run = CreateRunAt(typeface, "Hamburg", 13, 8.3);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                surface.Canvas.Clear(SKColors.White);

                using var paint = new SKPaint { Color = SKColors.Black };
                var blob = Avalonia.Skia.NativeTextBlob.TryGetTextBlob(run,
                    new TextOptions { TextRenderingMode = TextRenderingMode.SubpixelAntialias, TextHintingMode = hinting },
                    default)!;

                surface.Canvas.DrawText(blob, 8, 34, paint);
            }

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var bytes = new byte[info.Width * info.Height * 4];

            System.Runtime.InteropServices.Marshal.Copy(readback.GetPixels(), bytes, 0, bytes.Length);

            return bytes;
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
                       SurfaceIsDisplay = true,
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

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(originX, 34));
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
                       SurfaceIsDisplay = true,
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

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(8, 34));
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
