using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace Avalonia.Skia.RenderTests
{
    internal static class TextRasterizationModuleInitializer
    {
        // The expectations in this assembly (goldens, ink bounds, routed tiers) are recorded
        // against managed rasterization. Tests that do not choose a mode would otherwise follow
        // the host's platform default and render through the backend's text stack on Linux,
        // macOS and ARM64 hosts. Tests that exercise the backend set it explicitly.
        [ModuleInitializer]
        internal static void PinManagedTextRasterization()
            => TextRasterizationDefaults.PlatformDefault = TextRasterizationMode.Managed;
    }
}
