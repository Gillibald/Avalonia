using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Debug-only visualization of glyph run tier routing: when enabled, every drawn run gets
    /// a translucent badge over its bounds colored by the tier that actually rendered it —
    /// green for composed run masks, magenta for the Slug vector tier, orange for the native
    /// blob fallback. Off by default; flipped by diagnostic tooling (the GlyphRasterDemo
    /// inspector), never by product code.
    /// </summary>
    internal static class TextTierDiagnostics
    {
        public static volatile bool TintTiers;

        /// <summary>Per-tier draw counters, gated separately from the visual tint so a HUD
        /// can watch routing without repainting badges. Reset by the tooling.</summary>
        public static volatile bool CountTiers;

        public static long MaskTierDraws;
        public static long SlugTierDraws;
        public static long BlobTierDraws;

        public static void ResetCounters()
        {
            System.Threading.Interlocked.Exchange(ref MaskTierDraws, 0);
            System.Threading.Interlocked.Exchange(ref SlugTierDraws, 0);
            System.Threading.Interlocked.Exchange(ref BlobTierDraws, 0);
        }

        public static readonly SKColor MaskTierColor = new(0x22, 0xAA, 0x22, 0x46);
        public static readonly SKColor SlugTierColor = new(0xCC, 0x22, 0x99, 0x46);
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
