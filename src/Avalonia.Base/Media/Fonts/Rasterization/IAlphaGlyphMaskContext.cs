using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The backend fast path for managed text: realize an untinted 8-bit coverage mask once and
    /// tint it per draw. Tint then leaves the run-mask identity entirely — a foreground color
    /// animation reuses one cached mask instead of recomposing per color — and mask memory drops
    /// to a quarter of the BGRA floor. A drawing context that does not implement this keeps the
    /// portable pre-tinted bitmap path.
    /// </summary>
    internal interface IAlphaGlyphMaskContext
    {
        /// <summary>
        /// Whether this context actually benefits from alpha masks. On a GPU-backed Skia
        /// canvas, modulating an A8 texture by paint color is the native mechanism; on the CPU
        /// raster pipeline the same draw measured ~6x slower than a pre-tinted BGRA blit, so
        /// software contexts keep the portable floor.
        /// </summary>
        bool PrefersAlphaMasks { get; }

        /// <summary>
        /// Realizes an immutable alpha mask (row-major, stride equal to <paramref name="width"/>).
        /// The handle is owned by the caller's cache — disposed on eviction, drawn via
        /// <see cref="DrawAlphaMask"/> — and must stay valid across frames and device loss.
        /// </summary>
        IDisposable CreateAlphaMask(ReadOnlySpan<byte> alpha, int width, int height);

        /// <summary>
        /// Draws a realized mask modulated by a straight (non-premultiplied) ARGB tint; the
        /// context's ambient opacity applies on top.
        /// </summary>
        void DrawAlphaMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb);
    }
}
