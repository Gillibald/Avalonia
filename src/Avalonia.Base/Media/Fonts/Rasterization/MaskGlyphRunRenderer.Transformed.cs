using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    internal static partial class MaskGlyphRunRenderer
    {
        /// <summary>
        /// Attempts to draw a run the upright triage rejected through transformed glyph masks.
        /// Returns <c>false</c> when this draw cannot take the run and the caller falls back.
        /// </summary>
        public static bool TryDrawTransformed(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, TextRenderingMode textRenderingMode)
        {
            return false;
        }
    }
}
