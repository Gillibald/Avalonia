using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// GPU-context coverage for the Slug tier on both Windows backends: a hidden-window WGL
    /// context (native desktop GL) and an ANGLE D3D11 pbuffer context created from the same
    /// av_libglesv2 binary the real application uses — so the full
    /// <see cref="DrawingContextImpl.DrawGlyphRun"/> path runs against what actually ships,
    /// not just against whatever the test machine's GL driver does. Skips cleanly where a
    /// backend is unavailable.
    /// </summary>
    public class SlugGpuRenderingTests
    {
        public enum Backend
        {
            NativeGl,
            Angle,
        }

        [Theory]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.TopLeft, 22.5, 60, 40)]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.BottomLeft, 22.5, 60, 40)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.TopLeft, 22.5, 60, 40)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.BottomLeft, 22.5, 60, 40)]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.TopLeft, 90, 100, 30)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.BottomLeft, 90, 100, 30)]
        public void Rotated_Managed_Text_Renders_On_A_Gpu_Canvas(
            Backend backend, GRSurfaceOrigin origin, double degrees, double translateX, double translateY)
        {
            using var gpu = GpuContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            var typeface = LoadTypeface();

            using var run = CreateRun(typeface, "gg", emSize: 48);

            var info = new SKImageInfo(240, 200, SKColorType.Bgra8888, SKAlphaType.Premul);

            // Bottom-left matches the application's window framebuffer orientation.
            using var surface = SKSurface.Create(gpu!.GrContext, true, info, 0, origin);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                   }))
            {
                // The GPU gate must be open — this is the configuration the application runs.
                Assert.True(((ISlugGlyphRunContext)context).SupportsSlugRendering);

                surface!.Canvas.Clear(SKColors.Transparent);
                context.Transform = Matrix.CreateRotation(Math.PI * degrees / 180) *
                    Matrix.CreateTranslation(translateX, translateY);
                context.DrawGlyphRun(Brushes.Black, run);
            }

            gpu.GrContext.Flush();

            using var snapshot = surface!.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var inked = 0;

            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    if (readback.GetPixel(x, y).Alpha > 32)
                    {
                        inked++;
                    }
                }
            }

            Assert.True(inked > 100,
                $"Rotated managed text produced only {inked} inked pixels on the {backend}/{origin} canvas.");
        }

        [Theory]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.TopLeft, 30, 50, -30)]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.BottomLeft, 30, 50, -30)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.TopLeft, 30, 50, -30)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.BottomLeft, 30, 50, -30)]
        [InlineData(Backend.NativeGl, GRSurfaceOrigin.TopLeft, 90, 170, 20)]
        [InlineData(Backend.Angle, GRSurfaceOrigin.BottomLeft, 90, 170, 20)]
        public void The_Gpu_Draw_Matches_The_Reference_Evaluator(
            Backend backend, GRSurfaceOrigin origin, double degrees, double translateX, double translateY)
        {
            using var gpu = GpuContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            const int width = 140;
            const int height = 140;
            const float emSize = 72f;
            const double baselineX = 30;
            const double baselineY = 100;

            var typeface = LoadTypeface();
            var store = typeface.SlugStore;
            var glyph = typeface.CharacterToGlyphMap['g'];

            Assert.True(store.TryRealize(typeface, glyph, out var placement));

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu!.GrContext, true, info, 0, origin);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var transform = Matrix.CreateRotation(Math.PI * degrees / 180) *
                Matrix.CreateTranslation(translateX, translateY);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                   }))
            {
                surface!.Canvas.Clear(SKColors.Transparent);
                context.Transform = transform;

                ((ISlugGlyphRunContext)context).DrawSlugGlyph(
                    store, in placement, baselineX, baselineY, emSize, 0xFFFFFFFF);
            }

            gpu.GrContext.Flush();

            using var snapshot = surface!.Snapshot();
            using var readback = new SKBitmap(info);

            Assert.True(snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0));

            var local = new SKMatrix(emSize, 0, (float)baselineX, 0, -emSize, (float)baselineY, 0, 0, 1);
            var emToDevice = SKMatrix.Concat(transform.ToSKMatrix(), local);

            Assert.True(emToDevice.TryInvert(out var deviceToEm));

            var emsPerPixelX = Math.Abs(deviceToEm.ScaleX) + Math.Abs(deviceToEm.SkewX);
            var emsPerPixelY = Math.Abs(deviceToEm.SkewY) + Math.Abs(deviceToEm.ScaleY);

            var sum = 0.0;
            var worst = 0.0;

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var em = deviceToEm.MapPoint(new SKPoint(px + 0.5f, py + 0.5f));

                    var expected = SlugReferenceEvaluator.Evaluate(
                        store.CurveTexels, store.BandTexels, in placement,
                        em.X, em.Y, emsPerPixelX, emsPerPixelY);

                    var delta = Math.Abs(readback.GetPixel(px, py).Alpha / 255.0 - expected);

                    sum += delta;
                    worst = Math.Max(worst, delta);
                }
            }

            var mean = sum / (width * height);

            // GPU float math may differ from the CPU reference by ULPs at edges; decisions may
            // not. Anything approaching a coverage misclassification (0.5) means broken texels
            // or broken sampling, which is exactly what this guards.
            Assert.True(mean <= 0.004 && worst <= 0.1,
                FormattableString.Invariant($"{backend}/{origin} vs evaluator: mean {mean:0.00000}, worst {worst:0.00000}"));
        }

        private sealed class GpuContext : IDisposable
        {
            private readonly Action _cleanup;

            private GpuContext(GRContext grContext, Action cleanup)
            {
                GrContext = grContext;
                _cleanup = cleanup;
            }

            public GRContext GrContext { get; }

            public static GpuContext? TryCreate(Backend backend, out string reason)
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

            private static GpuContext? TryCreateWgl(out string reason)
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
                    Flags = 0x4 | 0x20 | 0x1,   // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
                    PixelType = 0,               // RGBA
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

                return new GpuContext(grContext, () =>
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(glContext);
                    ReleaseDC(window, dc);
                    DestroyWindow(window);
                });
            }

            private static GpuContext? TryCreateAngle(out string reason)
            {
                // The same combined ANGLE binary Avalonia ships (EGL entry points included),
                // taken from the NuGet cache so the test needs no packaging changes.
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

                // ANGLE's combined library exports the EGL entry points with an EGL_ prefix
                // (the unprefixed spellings live in the separate libEGL forwarder, which the
                // Avalonia package does not ship).
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

                if (display == IntPtr.Zero)
                {
                    reason = "eglGetDisplay returned no display";
                    return null;
                }

                if (!eglInitialize(display, out _, out _))
                {
                    reason = "eglInitialize failed";
                    return null;
                }

                // RGBA8888 + stencil, pbuffer, ES3-renderable (ES2 retry below).
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

                var surface = eglCreatePbufferSurface(display, configs[0], new[] { 0x3057, 4, 0x3056, 4, 0x3038 });

                if (surface == IntPtr.Zero)
                {
                    reason = "eglCreatePbufferSurface failed";
                    return null;
                }

                var context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { 0x3098, 3, 0x3038 });

                if (context == IntPtr.Zero)
                {
                    context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { 0x3098, 2, 0x3038 });
                }

                if (context == IntPtr.Zero || !eglMakeCurrent(display, surface, surface, context))
                {
                    reason = "eglCreateContext / eglMakeCurrent failed";
                    return null;
                }

                var glInterface = GRGlInterface.CreateGles(name => eglGetProcAddress(name));
                var grContext = glInterface is null ? null : GRContext.CreateGl(glInterface);

                if (grContext is null)
                {
                    reason = glInterface is null ? "GRGlInterface.CreateGles returned null" : "GRContext.CreateGl returned null";
                    eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    return null;
                }

                // The display stays initialized (ANGLE shares it process-wide); only the
                // binding is released so the next backend can go current on this thread.
                return new GpuContext(grContext,
                    () => eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }
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

            return new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(10, 60));
        }

        private static GlyphTypeface LoadTypeface()
        {
            var bytes = LoadFontBytes("Inter-Regular.ttf");
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PixelFormatDescriptor
        {
            public ushort Size;
            public ushort Version;
            public uint Flags;
            public byte PixelType;
            public byte ColorBits;
            public byte RedBits;
            public byte RedShift;
            public byte GreenBits;
            public byte GreenShift;
            public byte BlueBits;
            public byte BlueShift;
            public byte AlphaBits;
            public byte AlphaShift;
            public byte AccumBits;
            public byte AccumRedBits;
            public byte AccumGreenBits;
            public byte AccumBlueBits;
            public byte AccumAlphaBits;
            public byte DepthBits;
            public byte StencilBits;
            public byte AuxBuffers;
            public byte LayerType;
            public byte Reserved;
            public uint LayerMask;
            public uint VisibleMask;
            public uint DamageMask;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr EglGetDisplay(IntPtr nativeDisplay);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EglInitialize(IntPtr display, out int major, out int minor);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EglChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs,
            int configSize, out int numConfig);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr EglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribs);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr EglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribs);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        private delegate IntPtr EglGetProcAddress(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

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
        private static extern bool wglDeleteContext(IntPtr context);

        [DllImport("opengl32.dll")]
        private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
    }
}
