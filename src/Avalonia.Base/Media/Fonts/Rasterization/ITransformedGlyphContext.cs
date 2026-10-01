using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What produces a drawing context's pixels. Transformed text picks its draw mechanism by
    /// it: a CPU raster pipeline shades every sampled pixel in software, a hardware GPU makes
    /// per-pixel work nearly free but charges per draw submission, and a software GPU driver
    /// sits between the two.
    /// </summary>
    internal enum GlyphRasterTarget : byte
    {
        /// <summary>The CPU raster pipeline, or a recording that is not bound to a GPU.</summary>
        Raster,

        /// <summary>
        /// A GPU API implemented on the CPU: llvmpipe, lavapipe, SwiftShader or WARP. Batched
        /// draws are cheap, while fragment shading costs CPU time per pixel.
        /// </summary>
        SoftwareGpu,

        /// <summary>A hardware GPU.</summary>
        HardwareGpu,
    }

    /// <summary>
    /// One sprite of an atlas batch: the source rectangle in the batch's image and the
    /// sprite's top-left in the batch's coordinate space.
    /// </summary>
    internal readonly record struct GlyphAtlasSprite(int SourceX, int SourceY, int Width, int Height, int X, int Y);

    /// <summary>
    /// The backend services the transformed text tier draws through.
    /// </summary>
    internal interface ITransformedGlyphContext
    {
        /// <summary>What rasterizes this context's output.</summary>
        GlyphRasterTarget RasterTarget { get; }

        /// <summary>
        /// Grants direct write access to the raster surface when a draw may bypass the backend:
        /// a CPU surface with no layer, no ambient opacity, no non-default blend mode and a
        /// rectangular clip, drawn at device scale. Returns <c>false</c> otherwise, and the
        /// caller draws through the backend.
        /// </summary>
        bool TryGetBlitTarget(out GlyphBlitTarget target);

        /// <summary>
        /// Draws a realized upright run mask from <see cref="IAlphaGlyphMaskContext"/> scaled
        /// to <paramref name="destRect"/> with bilinear sampling, modulated by the straight ARGB
        /// <paramref name="tintArgb"/> and the ambient opacity: the stand-in for rasterizing
        /// during a zoom gesture. <paramref name="lcd"/> selects a subpixel mask.
        /// </summary>
        void DrawMaskStretched(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb, bool lcd);

        /// <summary>
        /// Realizes the sprite arrays of one batch, sized exactly to <paramref name="sprites"/>
        /// so drawing the batch allocates nothing. With <paramref name="standalone"/> the batch
        /// draws from its own image of that mask (a glyph too large for an atlas page) instead
        /// of the batch's page. The caller owns the result and disposes it with the batch.
        /// </summary>
        IDisposable CreateAtlasBatch(ReadOnlySpan<GlyphAtlasSprite> sprites, GlyphMask? standalone);

        /// <summary>
        /// Draws a batch in one call, its sprites mapped through <paramref name="transform"/>
        /// (batch space to device pixels), modulated by the straight ARGB
        /// <paramref name="tintArgb"/> and the ambient opacity. The stored coverage is already
        /// corrected for the tint, so the draw adds no correction of its own.
        /// <paramref name="bilinear"/> samples bilinearly, which a batch drawn under a scaling
        /// or rotating transform needs.
        /// </summary>
        void DrawAtlasBatch(GlyphAtlasBatch batch, in Matrix transform, uint tintArgb, bool bilinear);
    }
}
