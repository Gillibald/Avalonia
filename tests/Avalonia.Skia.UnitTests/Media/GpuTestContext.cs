using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia.Vulkan;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia.UnitTests.Media
{
    public enum GpuBackend
    {
        NativeGl,
        Angle,
        Metal,
        Vulkan,
    }

    /// <summary>
    /// A Skia GPU context on one of the Windows backends: a hidden-window WGL context (native
    /// desktop GL) or an ANGLE D3D11 pbuffer context created from the same av_libglesv2 binary
    /// the real application uses, so GPU tests run against what actually ships; on macOS, a
    /// Metal context on the system default device, the backend Avalonia.Native renders with; on
    /// Windows and Linux, the Vulkan backend's own GPU on a device of the system's loader.
    /// Creation returns <c>null</c> with a reason where a backend is unavailable, for the caller
    /// to skip.
    /// </summary>
    internal sealed class GpuTestContext : IDisposable
    {
        private readonly Action _cleanup;
        private readonly bool _ownsGrContext;

        private GpuTestContext(GRContext grContext, Action cleanup, Func<string, IntPtr>? getGlProcAddress = null,
            int glMajorVersion = 0)
        {
            GrContext = grContext;
            _cleanup = cleanup;
            _ownsGrContext = true;
            GetGlProcAddress = getGlProcAddress;
            GlMajorVersion = glMajorVersion;
        }

        private GpuTestContext(VulkanSkiaGpu vulkan, IDisposable deviceLock)
        {
            GrContext = vulkan.GrContext;
            UpdatableTextures = (ISkiaUpdatableTextureFeature?)vulkan.TryGetFeature(typeof(ISkiaUpdatableTextureFeature));

            BackendMaskAtlas = SkiaUpdatableTextures.Get(GrContext)?.MaskAtlas;

            // Like the GL contexts, the context starts unregistered; tests register it with an
            // atlas of their own.
            SkiaUpdatableTextures.Register(GrContext, null);
            _cleanup = () =>
            {
                vulkan.Dispose();
                deviceLock.Dispose();
            };
        }

        public GRContext GrContext { get; }

        /// <summary>
        /// The updatable textures a backend GPU object offers for its context, registered by the
        /// caller as that backend registers them; <c>null</c> for GL contexts and where the
        /// backend has none.
        /// </summary>
        public ISkiaUpdatableTextureFeature? UpdatableTextures { get; }

        /// <summary>The glyph mask atlas the backend GPU object registered for its context, if any.</summary>
        public GlyphMaskAtlas? BackendMaskAtlas { get; }

        /// <summary>Resolves GL entry points of a GL context; <c>null</c> for other backends.</summary>
        public Func<string, IntPtr>? GetGlProcAddress { get; }

        /// <summary>The major version of the GL or GLES context, 0 for other backends.</summary>
        public int GlMajorVersion { get; }

        public static GpuTestContext? TryCreate(GpuBackend backend, out string reason)
        {
            if (backend == GpuBackend.Metal)
            {
                reason = "not macOS";

                return OperatingSystem.IsMacOS() ? TryCreateMetal(out reason) : null;
            }

            if (backend == GpuBackend.Vulkan)
            {
                // macOS libSkiaSharp is built without Vulkan.
                reason = "macOS";

                return OperatingSystem.IsMacOS() ? null : TryCreateVulkan(out reason);
            }

            reason = "not Windows";

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return backend == GpuBackend.NativeGl ? TryCreateWgl(out reason) : TryCreateAngle(out reason);
            }
            catch (Exception e)
            {
                reason = e.Message;
                return null;
            }
        }

        public void Dispose()
        {
            if (_ownsGrContext)
            {
                GrContext.Dispose();
            }

            _cleanup();
        }

        private static readonly object s_vulkanLock = new();
        private static IVulkanPlatformGraphicsContext? s_vulkan;
        private static string? s_vulkanFailure;

        /// <summary>
        /// A context on one device of the process, which lives as long as the process: Skia
        /// images of a disposed context may still be released later and free their memory on
        /// that device. Each test context holds the device lock from creation to disposal, as
        /// Avalonia's renderer does while it draws, so Vulkan tests run one at a time and never
        /// submit to the device's queue from two threads.
        /// </summary>
        private static GpuTestContext? TryCreateVulkan(out string reason)
        {
            lock (s_vulkanLock)
            {
                if (s_vulkan is null && s_vulkanFailure is null)
                {
                    s_vulkan = CreateVulkanGraphics(out s_vulkanFailure)?.CreateContext() as IVulkanPlatformGraphicsContext;
                    s_vulkanFailure ??= s_vulkan is null ? "Vulkan context creation failed" : null;
                }
            }

            reason = s_vulkanFailure ?? string.Empty;

            if (s_vulkan is not { } context)
            {
                return null;
            }

            var deviceLock = context.Device.Lock();

            try
            {
                return new GpuTestContext(new VulkanSkiaGpu(context, null, null), deviceLock);
            }
            catch (Exception e)
            {
                reason = e.Message;
                deviceLock.Dispose();
                return null;
            }
        }

        private static VulkanPlatformGraphics? CreateVulkanGraphics(out string? failure)
        {
            failure = "no Vulkan loader";

            if (!NativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "vulkan-1" : "libvulkan.so.1", out var loader) ||
                !NativeLibrary.TryGetExport(loader, "vkGetInstanceProcAddr", out var getInstanceProcAddr))
            {
                return null;
            }

            var graphics = VulkanPlatformGraphics.TryCreate(
                new VulkanOptions
                {
                    VulkanInstanceCreationOptions = { RequireSurfaceExtension = false },
                    VulkanDeviceCreationOptions = { RequireSwapchainExtension = false, PreferDiscreteGpu = true },
                },
                new VulkanPlatformSpecificOptions
                {
                    GetProcAddressDelegate = (instance, name) => GetInstanceProcAddress(getInstanceProcAddr, instance, name),
                });

            failure = graphics is null ? "Vulkan initialization failed" : null;

            return graphics;
        }

        private static unsafe IntPtr GetInstanceProcAddress(IntPtr getInstanceProcAddr, IntPtr instance, string name)
        {
            var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];

            System.Text.Encoding.UTF8.GetBytes(name, bytes.AsSpan(0, bytes.Length - 1));

            fixed (byte* p = bytes)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr>)getInstanceProcAddr)(instance, p);
            }
        }

        private static GpuTestContext? TryCreateMetal(out string reason)
        {
            reason = "no Metal device";

            var device = MTLCreateSystemDefaultDevice();

            if (device == IntPtr.Zero)
            {
                return null;
            }

            var queue = objc_msgSend(device, sel_registerName("newCommandQueue"));

            reason = "Metal context creation failed";

            var grContext = GRContext.CreateMetal(new GRMtlBackendContext { DeviceHandle = device, QueueHandle = queue });

            if (grContext is null)
            {
                objc_msgSend(queue, sel_registerName("release"));
                objc_msgSend(device, sel_registerName("release"));
                return null;
            }

            return new GpuTestContext(grContext, () =>
            {
                objc_msgSend(queue, sel_registerName("release"));
                objc_msgSend(device, sel_registerName("release"));
            });
        }

        [DllImport("/System/Library/Frameworks/Metal.framework/Metal")]
        private static extern IntPtr MTLCreateSystemDefaultDevice();

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        private static GpuTestContext? TryCreateWgl(out string reason)
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

            // wglGetProcAddress resolves extension and post-1.1 entry points only; the 1.1 ones are
            // exports of opengl32.dll.
            var opengl32 = NativeLibrary.Load("opengl32.dll");
            Func<string, IntPtr> getProcAddress = name =>
            {
                var address = wglGetProcAddress(name);

                return address is 0 or 1 or 2 or 3 or -1
                    ? NativeLibrary.TryGetExport(opengl32, name, out var export) ? export : IntPtr.Zero
                    : address;
            };

            return new GpuTestContext(grContext, () =>
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(glContext);
                ReleaseDC(window, dc);
                DestroyWindow(window);
            }, getProcAddress, ReadGlMajorVersion(getProcAddress));
        }

        private static unsafe int ReadGlMajorVersion(Func<string, IntPtr> getProcAddress)
        {
            const int glVersion = 0x1F02;

            var getString = (delegate* unmanaged[Stdcall]<int, byte*>)getProcAddress("glGetString");
            var version = Marshal.PtrToStringAnsi((IntPtr)getString(glVersion)) ?? string.Empty;
            var digits = version.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray();

            return digits.Length > 0 ? int.Parse(new string(digits)) : 0;
        }

        private static GpuTestContext? TryCreateAngle(out string reason)
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
            var major = 3;

            if (context == IntPtr.Zero)
            {
                context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { 0x3098, 2, 0x3038 });
                major = 2;
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
            return new GpuTestContext(grContext,
                () => eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero),
                name => eglGetProcAddress(name), major);
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

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr wglGetProcAddress(string name);
    }
}
