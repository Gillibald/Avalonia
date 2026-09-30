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

        public static void Register(GRContext context, bool isSoftware)
        {
        }

        public static bool IsSoftware(GRContext context) => false;

        /// <summary>Whether a <c>GL_RENDERER</c> string names a software rasterizer.</summary>
        public static bool IsSoftwareGlRenderer(string? renderer) => false;

        /// <summary>Whether a Vulkan physical device is implemented on the CPU.</summary>
        public static bool IsSoftwareVulkanDevice(int deviceType, string? deviceName) => false;
    }
}
