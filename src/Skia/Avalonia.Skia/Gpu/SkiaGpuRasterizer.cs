using System;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Remembers which GPU contexts run on a software implementation of their graphics API.
    /// The GPU wrappers classify their device once, when they create the context, from the GL
    /// renderer string or the Vulkan device type; drawing contexts look the answer up per
    /// <see cref="GRContext"/>. A context nobody classified counts as hardware.
    /// </summary>
    internal static class SkiaGpuRasterizer
    {
        /// <summary>The Vulkan <c>VK_PHYSICAL_DEVICE_TYPE_CPU</c> device type.</summary>
        public const int VulkanCpuDeviceType = 4;

        // Mesa's llvmpipe, softpipe, swrast and lavapipe, Google's SwiftShader (also behind
        // ANGLE), Microsoft's WARP as ANGLE names it, and the Windows GDI OpenGL 1.1 fallback.
        private static readonly string[] s_softwareNames =
        {
            "llvmpipe", "softpipe", "swrast", "lavapipe", "SwiftShader", "Microsoft Basic Render Driver",
            "GDI Generic",
        };

        private static readonly ConditionalWeakTable<GRContext, object> s_software = new();
        private static readonly object s_marker = new();

        /// <summary>Records how <paramref name="context"/> was classified.</summary>
        public static void Register(GRContext context, bool isSoftware)
        {
            if (isSoftware)
            {
                s_software.AddOrUpdate(context, s_marker);
            }
            else
            {
                s_software.Remove(context);
            }
        }

        /// <summary>Whether <paramref name="context"/> was registered as a software rasterizer.</summary>
        public static bool IsSoftware(GRContext context) => s_software.TryGetValue(context, out _);

        /// <summary>Whether a <c>GL_RENDERER</c> string names a software rasterizer.</summary>
        public static bool IsSoftwareGlRenderer(string? renderer) => ContainsSoftwareName(renderer);

        /// <summary>Whether a Vulkan physical device is implemented on the CPU.</summary>
        public static bool IsSoftwareVulkanDevice(int deviceType, string? deviceName)
            => deviceType == VulkanCpuDeviceType || ContainsSoftwareName(deviceName);

        private static bool ContainsSoftwareName(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (var software in s_softwareNames)
            {
                if (name.Contains(software, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
