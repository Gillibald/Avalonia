using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Debug-only visualization of glyph run tier routing: with <see cref="SkiaOptions.TintTextTiers"/>,
    /// every drawn run gets a translucent badge over its bounds colored by the tier that actually
    /// rendered it: green for upright run masks, blue for transformed run masks, orange for the
    /// native blob fallback. With <see cref="SkiaOptions.CountTextTiers"/>, per-tier draw counters,
    /// gated separately from the visual tint so a HUD can watch routing without repainting badges.
    /// Off by default; set by diagnostic tooling, never by product code.
    /// </summary>
    internal static class TextTierDiagnostics
    {
        public static long MaskTierDraws;
        public static long TransformedMaskTierDraws;
        public static long BlobTierDraws;

        public static void ResetCounters()
        {
            System.Threading.Interlocked.Exchange(ref MaskTierDraws, 0);
            System.Threading.Interlocked.Exchange(ref TransformedMaskTierDraws, 0);
            System.Threading.Interlocked.Exchange(ref BlobTierDraws, 0);
        }

        public static readonly SKColor MaskTierColor = new(0x22, 0xAA, 0x22, 0x46);
        public static readonly SKColor TransformedMaskTierColor = new(0x22, 0x66, 0xDD, 0x46);
        public static readonly SKColor BlobTierColor = new(0xDD, 0x88, 0x22, 0x46);

        /// <summary>Fills the run's bounds with the tier color under the current transform.</summary>
        public static void DrawBadge(SKCanvas canvas, Rect bounds, SKColor color)
        {
            using var paint = new SKPaint();

            paint.Color = color;
            canvas.DrawRect((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height, paint);
        }
    }
}
