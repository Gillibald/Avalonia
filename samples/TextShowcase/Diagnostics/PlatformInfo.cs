using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;

namespace TextShowcase.Diagnostics
{
    /// <summary>What the machine renders with, for the header and capture manifests.</summary>
    internal static class PlatformInfo
    {
        private static string? s_graphics;
        private static string? s_renderer;

        public static string OsName =>
            OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsAndroid() ? "Android"
            : OperatingSystem.IsIOS() ? "iOS"
            : OperatingSystem.IsBrowser() ? "Browser"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Other";

        public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        /// <summary>The scaler Skia's typefaces use on this platform when they are created from font data.</summary>
        public static string BackendScaler =>
            OperatingSystem.IsWindows() ? "DirectWrite"
            : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "CoreText"
            : "FreeType";

        /// <summary>The mode an application gets on this platform when it does not choose one.</summary>
        public static TextRasterizationMode PlatformDefault => TextRasterizationDefaults.PlatformDefault;

        /// <summary>A short name of the GPU path: ANGLE, WGL, Vulkan, Software and so on.</summary>
        public static string Graphics
        {
            get
            {
                Resolve();
                return s_graphics!;
            }
        }

        /// <summary>The GL renderer string where there is one, otherwise the graphics name.</summary>
        public static string Renderer
        {
            get
            {
                Resolve();
                return s_renderer!;
            }
        }

        private static void Resolve()
        {
            if (s_graphics is not null)
            {
                return;
            }

            var graphics = AvaloniaLocator.Current.GetService<IPlatformGraphics>();
            var name = graphics?.GetType().Name ?? "Software";

            s_graphics = name switch
            {
                _ when name.Contains("Angle", StringComparison.OrdinalIgnoreCase) => "ANGLE",
                _ when name.Contains("Wgl", StringComparison.OrdinalIgnoreCase) => "WGL",
                _ when name.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) => "Vulkan",
                _ when name.Contains("Metal", StringComparison.OrdinalIgnoreCase) => "Metal",
                _ when name.Contains("Egl", StringComparison.OrdinalIgnoreCase) => "EGL",
                _ when name.Contains("Glx", StringComparison.OrdinalIgnoreCase) => "GLX",
                _ => name.Replace("PlatformGraphics", "", StringComparison.Ordinal),
            };
            s_renderer = s_graphics;

            if (graphics is { UsesSharedContext: true })
            {
                try
                {
                    if (graphics.GetSharedContext() is IGlContext gl && gl.GlInterface.Renderer is { } renderer)
                    {
                        s_renderer = renderer;
                    }
                }
                catch (Exception)
                {
                    // The renderer string is decoration; the graphics name stands in for it.
                }
            }
        }
    }
}
