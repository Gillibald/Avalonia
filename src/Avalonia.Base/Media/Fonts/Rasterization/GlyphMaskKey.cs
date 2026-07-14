using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>The rendering mode a glyph mask was rasterized with.</summary>
    internal enum GlyphMaskMode : byte
    {
        /// <summary>Grayscale antialiased coverage.</summary>
        Antialiased = 0,

        /// <summary>Coverage thresholded at one half (TextRenderingMode.Alias).</summary>
        Aliased = 1,

        /// <summary>Per-stripe LCD coverage (TextRenderingMode.SubpixelAntialias), three
        /// channels interleaved; only built when the destination is LCD-eligible.</summary>
        Subpixel = 2,
    }

    /// <summary>
    /// Cache identity of a rasterized glyph mask. Scale is quantized to 1/8 px-per-em steps so
    /// floating-point noise in transform math cannot mint spurious variants; the subpixel x
    /// phase is bucketed to quarter pixels (y rides baseline snapping and has no phase). Neither
    /// opacity nor foreground tint is part of the identity: opacity rides the draw call's own
    /// parameter and tint variants are a run-mask concern, so animating either never touches
    /// this cache.
    /// </summary>
    internal readonly record struct GlyphMaskKey(ushort Glyph, ushort ScaleQ, byte Phase, GlyphMaskMode Mode)
    {
        /// <summary>Number of subpixel x-phase buckets.</summary>
        public const int PhaseCount = 4;

        /// <summary>Scale quantization steps per device pixel of em size.</summary>
        public const float ScaleQuantum = 8f;

        /// <summary>The quantized device pixels per em this mask was rasterized at.</summary>
        public float PixelsPerEm => ScaleQ / ScaleQuantum;

        /// <summary>The subpixel x offset this mask's coverage was sampled at.</summary>
        public float PhaseOffset => Phase * (1f / PhaseCount);

        public static GlyphMaskKey Create(ushort glyph, float pixelsPerEm, float penX, GlyphMaskMode mode)
        {
            SnapPen(penX, out _, out var phase);
            return new GlyphMaskKey(glyph, QuantizeScale(pixelsPerEm), phase, mode);
        }

        /// <summary>Quantizes a device px-per-em value to the cache's scale grid (min one step).</summary>
        public static ushort QuantizeScale(float pixelsPerEm)
        {
            var q = (int)MathF.Round(pixelsPerEm * ScaleQuantum);

            return (ushort)Math.Clamp(q, 1, ushort.MaxValue);
        }

        /// <summary>
        /// Splits a fractional device pen x into the integer pixel the mask is placed at and the
        /// nearest quarter-pixel phase bucket. Rounding is to the nearest quarter overall, so
        /// e.g. x = 5.95 snaps to pixel 6 with phase 0, not pixel 5 with a wrapped phase.
        /// </summary>
        public static void SnapPen(float penX, out int pixelX, out byte phase)
        {
            var q = (int)MathF.Round(penX * PhaseCount);

            pixelX = q >> 2;
            phase = (byte)(q & (PhaseCount - 1));
        }
    }
}
