using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Skia.UnitTests
{
    internal static class TextRasterizationModuleInitializer
    {
        // The expectations in this assembly (goldens, ink bounds, routed tiers) are recorded
        // against managed rasterization. Tests that do not choose a mode would otherwise follow
        // the host's platform default and render through the backend's text stack on Linux,
        // macOS and ARM64 hosts. Tests that exercise the backend set it explicitly.
        [ModuleInitializer]
        internal static void PinManagedTextRasterization()
        {
            TextRasterizationDefaults.PlatformDefault = TextRasterizationMode.Managed;

            // Unspecified text renders subpixel on display surfaces in the recorded expectations;
            // macOS and iOS hosts would otherwise render it grayscale.
            TextRasterizationDefaults.UnspecifiedRendersSubpixel = true;

            // A GPU draws a moving run sharp while the frame's rasterization budget affords it,
            // which depends on how fast the machine rasterizes. The expectations here record
            // the stretched frames of an exhausted budget; tests of the budget inject their own.
            MotionRasterBudget.BudgetOverride = 0;
        }
    }
}
