using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>Stripe geometries the subpixel mask path can serve.</summary>
    internal enum LcdMaskGeometry : byte
    {
        RgbHorizontal = 0,
        BgrHorizontal = 1,
    }

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

        /// <summary>
        /// Realizes an immutable subpixel coverage mask: RGBA8888 rows holding the three
        /// filtered stripe coverages in the destination's channel positions plus their maximum
        /// in alpha. Same ownership contract as <see cref="CreateAlphaMask"/>.
        /// </summary>
        IDisposable CreateLcdMask(ReadOnlySpan<byte> rgba, int width, int height);

        /// <summary>
        /// Draws a realized subpixel mask blended per channel with a straight ARGB tint; the
        /// context's ambient opacity applies on top. Only called when
        /// <see cref="TryGetLcdGeometry"/> reported eligibility for this draw.
        /// </summary>
        void DrawLcdMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb);

        /// <summary>
        /// Whether this draw may render subpixel (LCD) text right now, and with which stripe
        /// order. False whenever a platform stack would degrade to grayscale: unknown or
        /// vertical pixel geometry, a target that disables subpixel text, or drawing inside
        /// any composited layer (opacity layer, opacity mask, effect, layer group), where
        /// per-channel coverage would bake fringes against a transparent backdrop.
        /// </summary>
        bool TryGetLcdGeometry(out LcdMaskGeometry geometry);
    }
}
