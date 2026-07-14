using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Real-GPU coverage for subpixel (LCD) text: the runtime blender against the CPU formula,
    /// fringe presence and polarity through the full managed dispatch, and grayscale purity
    /// when the mode or the destination vetoes. The GPU bootstrap is a private copy of the
    /// Slug suite's (unify once a third consumer appears).
    /// </summary>
    public class LcdGpuRenderingTests
    {
        public enum Backend
        {
            NativeGl,
            Angle,
        }

        [Theory]
        [InlineData(Backend.NativeGl)]
        [InlineData(Backend.Angle)]
        public void The_Blender_Matches_The_Cpu_Formula(Backend backend)
        {
            using var gpu = GpuHarness.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            const uint tint = 0xFFCC2010;   // opaque reddish
            var background = new SKColor(0x40, 0x80, 0xC0);

            // Four pixels spanning empty, partial ramps, and full coverage (RGBA channel order).
            var coverage = new byte[]
            {
                0, 0, 0, 0,
                64, 128, 192, 192,
                192, 128, 64, 192,
                255, 255, 255, 255,
            };

            var info = new SKImageInfo(4, 1, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu!.GrContext, true, info, 0, GRSurfaceOrigin.TopLeft,
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal), false);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            using var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                GrContext = gpu.GrContext,
                Dpi = new Vector(96, 96),
            });

            surface!.Canvas.Clear(background);

            var alphaContext = (IAlphaGlyphMaskContext)context;

            Assert.True(alphaContext.TryGetLcdGeometry(out var geometry));
            Assert.Equal(LcdMaskGeometry.RgbHorizontal, geometry);

            using var mask = alphaContext.CreateLcdMask(coverage, 4, 1);

            alphaContext.DrawLcdMask(mask, new Rect(0, 0, 4, 1), new Rect(0, 0, 4, 1), tint);
            gpu.GrContext.Flush();

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var parameters = MaskGamma.GetShaderParameters(0xCC, 0x20, 0x10);

            for (var x = 0; x < 4; x++)
            {
                var expectedR = BlendChannel(coverage[x * 4], 0xCC, background.Red, parameters);
                var expectedG = BlendChannel(coverage[x * 4 + 1], 0x20, background.Green, parameters);
                var expectedB = BlendChannel(coverage[x * 4 + 2], 0x10, background.Blue, parameters);
                var actual = readback.GetPixel(x, 0);

                // pow in half precision plus 8-bit rounding: a few levels of slack.
                Assert.True(Math.Abs(actual.Red - expectedR) <= 4 &&
                            Math.Abs(actual.Green - expectedG) <= 4 &&
                            Math.Abs(actual.Blue - expectedB) <= 4,
                    $"pixel {x}: expected ({expectedR},{expectedG},{expectedB}), got {actual}");
            }
        }

        [Theory]
        [InlineData(Backend.NativeGl)]
        [InlineData(Backend.Angle)]
        public void Managed_Text_Fringes_With_The_Right_Polarity_And_Grayscale_Stays_Pure(Backend backend)
        {
            using var gpu = GpuHarness.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            var typeface = LoadTypeface();

            using var run = CreateRun(typeface, "HHH", emSize: 24);

            var info = new SKImageInfo(120, 48, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu!.GrContext, true, info, 0, GRSurfaceOrigin.TopLeft,
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal), false);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var lcdFringes = RenderAndCountFringes(gpu, surface!, info, run, TextRenderingMode.SubpixelAntialias,
                out var warmLeft, out var coolRight);

            Assert.True(lcdFringes > 20, $"expected fringing edge pixels, found {lcdFringes}");

            // Dark text on white, RGB stripes: entering ink covers the blue-side stripes of the
            // preceding pixel first, so left edges render warm (R brighter) and right edges
            // cool (B brighter). Channel-order bugs flip this even when diffs look plausible.
            Assert.True(warmLeft > 0, "no warm left-edge pixels — stripe order looks swapped");
            Assert.True(coolRight > 0, "no cool right-edge pixels — stripe order looks swapped");

            var grayFringes = RenderAndCountFringes(gpu, surface!, info, run, TextRenderingMode.Antialias,
                out _, out _);

            Assert.Equal(0, grayFringes);
        }

        private static int RenderAndCountFringes(GpuHarness gpu, SKSurface surface, SKImageInfo info,
            ManagedGlyphRunImpl run, TextRenderingMode mode, out int warmLeft, out int coolRight)
        {
            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface.Canvas.Clear(SKColors.White);
                context.PushRenderOptions(new RenderOptions { TextRenderingMode = mode });
                context.DrawGlyphRun(Brushes.Black, run);
                context.PopRenderOptions();
            }

            gpu.GrContext.Flush();

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
                        warmLeft++;   // warm pixel with the ink to its right = a left edge
                    }

                    if (pixel.Blue > pixel.Red && leftNeighborInk && !rightNeighborInk)
                    {
                        coolRight++;  // cool pixel with the ink to its left = a right edge
                    }
                }
            }

            return fringes;
        }

        private static int Luma(SKColor color) => 54 * color.Red + 183 * color.Green + 19 * color.Blue;

        private static byte BlendChannel(byte coverage, byte tint, byte dst,
            MaskGamma.GammaShaderParameters parameters)
        {
            var c = coverage / 255.0;
            var boosted = c + (1.0 - c) * parameters.Contrast * c;
            double corrected;

            if (parameters.NearEqual)
            {
                corrected = boosted;
            }
            else
            {
                var linOut = parameters.LinSrc * boosted + (1.0 - boosted) * parameters.LinDst;
                var output = Math.Pow(linOut, 1.0 / MaskGamma.Gamma);

                corrected = Math.Clamp((output - parameters.LumDst) / (parameters.LumSrc - parameters.LumDst), 0.0, 1.0);
            }

            return (byte)Math.Clamp(Math.Round(tint * corrected + dst * (1.0 - corrected)), 0, 255);
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

        private sealed class GpuHarness : IDisposable
        {
            private readonly Action _cleanup;

            private GpuHarness(GRContext grContext, Action cleanup)
            {
                GrContext = grContext;
                _cleanup = cleanup;
            }

            public GRContext GrContext { get; }

            public static GpuHarness? TryCreate(Backend backend, out string reason)
            {
                reason = "not Windows";

                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                try
                {
                    return backend == Backend.NativeGl ? TryCreateWgl(out reason) : TryCreateAngle(out reason);
                }
                catch (Exception e)
                {
                    reason = e.Message;
                    return null;
                }
            }

            public void Dispose()
            {
                GrContext.Dispose();
                _cleanup();
            }

            private static GpuHarness? TryCreateWgl(out string reason)
            {
                reason = "window creation failed";

                var window = CreateWindowExW(0, "STATIC", string.Empty, 0, 0, 0, 4, 4,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (window == IntPtr.Zero)
                {
                    return null;
                }

                reason = "pixel format / context / interface failed";

                var dc = GetDC(window);

                var descriptor = new PixelFormatDescriptor
                {
                    Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
                    Version = 1,
                    Flags = 0x4 | 0x20 | 0x1,
                    PixelType = 0,
                    ColorBits = 32,
                    StencilBits = 8,
                };

                var format = ChoosePixelFormat(dc, ref descriptor);

                if (format == 0 || !SetPixelFormat(dc, format, ref descriptor))
                {
                    ReleaseDC(window, dc);
                    DestroyWindow(window);
                    return null;
                }

                var glContext = wglCreateContext(dc);

                if (glContext == IntPtr.Zero || !wglMakeCurrent(dc, glContext))
                {
                    ReleaseDC(window, dc);
                    DestroyWindow(window);
                    return null;
                }

                var glInterface = GRGlInterface.Create();
                var grContext = glInterface is null ? null : GRContext.CreateGl(glInterface);

                if (grContext is null)
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(glContext);
                    ReleaseDC(window, dc);
                    DestroyWindow(window);
                    return null;
                }

                return new GpuHarness(grContext, () =>
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(glContext);
                    ReleaseDC(window, dc);
                    DestroyWindow(window);
                });
            }

            private static GpuHarness? TryCreateAngle(out string reason)
            {
                var packages = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages", "avalonia.angle.windows.natives");

                reason = "avalonia.angle.windows.natives not in the NuGet cache";

                if (!Directory.Exists(packages))
                {
                    return null;
                }

                var dll = Directory.GetDirectories(packages)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "runtimes", "win-x64", "native", "av_libglesv2.dll"))
                    .FirstOrDefault(File.Exists);

                if (dll is null || !NativeLibrary.TryLoad(dll, out var lib))
                {
                    reason = "av_libglesv2.dll missing or failed to load";
                    return null;
                }

                T Get<T>(string name) where T : Delegate
                    => Marshal.GetDelegateForFunctionPointer<T>(
                        NativeLibrary.TryGetExport(lib, "EGL_" + name.Substring(3), out var export)
                            ? export
                            : NativeLibrary.GetExport(lib, name));

                var eglGetDisplay = Get<EglGetDisplay>("eglGetDisplay");
                var eglInitialize = Get<EglInitialize>("eglInitialize");
                var eglChooseConfig = Get<EglChooseConfig>("eglChooseConfig");
                var eglCreatePbufferSurface = Get<EglCreatePbufferSurface>("eglCreatePbufferSurface");
                var eglCreateContext = Get<EglCreateContext>("eglCreateContext");
                var eglMakeCurrent = Get<EglMakeCurrent>("eglMakeCurrent");
                var eglGetProcAddress = Get<EglGetProcAddress>("eglGetProcAddress");

                var display = eglGetDisplay(IntPtr.Zero);

                if (display == IntPtr.Zero || !eglInitialize(display, out _, out _))
                {
                    reason = "eglGetDisplay / eglInitialize failed";
                    return null;
                }

                var configAttribs = new[]
                {
                    0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3026, 8,
                    0x3033, 0x0001, 0x3040, 0x0040, 0x3038,
                };
                var configs = new IntPtr[1];

                if (!eglChooseConfig(display, configAttribs, configs, 1, out var configCount) || configCount < 1)
                {
                    configAttribs[13] = 0x0004;

                    if (!eglChooseConfig(display, configAttribs, configs, 1, out configCount) || configCount < 1)
                    {
                        reason = "eglChooseConfig found no config";
                        return null;
                    }
                }

                var pbuffer = eglCreatePbufferSurface(display, configs[0], new[] { 0x3057, 4, 0x3056, 4, 0x3038 });

                if (pbuffer == IntPtr.Zero)
                {
                    reason = "eglCreatePbufferSurface failed";
                    return null;
                }

                var context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { 0x3098, 3, 0x3038 });

                if (context == IntPtr.Zero)
                {
                    context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { 0x3098, 2, 0x3038 });
                }

                if (context == IntPtr.Zero || !eglMakeCurrent(display, pbuffer, pbuffer, context))
                {
                    reason = "eglCreateContext / eglMakeCurrent failed";
                    return null;
                }

                var glInterface = GRGlInterface.CreateGles(name => eglGetProcAddress(name));
                var grContext = glInterface is null ? null : GRContext.CreateGl(glInterface);

                if (grContext is null)
                {
                    reason = "GRGlInterface / GRContext creation failed";
                    eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    return null;
                }

                return new GpuHarness(grContext,
                    () => eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PixelFormatDescriptor
            {
                public ushort Size;
                public ushort Version;
                public uint Flags;
                public byte PixelType;
                public byte ColorBits;
                public byte RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift;
                public byte AlphaBits, AlphaShift;
                public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
                public byte DepthBits;
                public byte StencilBits;
                public byte AuxBuffers;
                public byte LayerType;
                public byte Reserved;
                public uint LayerMask, VisibleMask, DamageMask;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
                uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
                IntPtr param);

            [DllImport("user32.dll")]
            private static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hwnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

            [DllImport("gdi32.dll")]
            private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor descriptor);

            [DllImport("gdi32.dll")]
            private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor descriptor);

            [DllImport("opengl32.dll")]
            private static extern IntPtr wglCreateContext(IntPtr dc);

            [DllImport("opengl32.dll")]
            private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);

            [DllImport("opengl32.dll")]
            private static extern bool wglDeleteContext(IntPtr context);

            private delegate IntPtr EglGetDisplay(IntPtr nativeDisplay);
            private delegate bool EglInitialize(IntPtr display, out int major, out int minor);
            private delegate bool EglChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs, int configSize, out int numConfigs);
            private delegate IntPtr EglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribs);
            private delegate IntPtr EglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attribs);
            private delegate bool EglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
            private delegate IntPtr EglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
        }
    }
}
