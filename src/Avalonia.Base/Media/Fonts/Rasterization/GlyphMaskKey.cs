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
    /// The linear part of a device transform a glyph mask is rasterized under, normalized to
    /// unit determinant (the scale lives in <see cref="GlyphMaskKey.ScaleQ"/>) and quantized to
    /// a 1/<see cref="Quantum"/> grid so floating-point noise cannot mint spurious variants.
    /// Each element is stored as its offset from the identity, so the default value is the
    /// identity and keys that never carry a transform keep their exact values.
    /// </summary>
    internal readonly record struct GlyphMaskTransform(short M11, short M12, short M21, short M22)
    {
        /// <summary>Quantization steps per unit of a matrix element.</summary>
        public const float Quantum = 4096f;

        /// <summary>Whether this is the upright, unscaled identity.</summary>
        public bool IsIdentity => M11 == 0 && M12 == 0 && M21 == 0 && M22 == 0;

        /// <summary>The dequantized first row, x column.</summary>
        public float Scale11 => 1f + M11 / Quantum;

        /// <summary>The dequantized first row, y column.</summary>
        public float Skew12 => M12 / Quantum;

        /// <summary>The dequantized second row, x column.</summary>
        public float Skew21 => M21 / Quantum;

        /// <summary>The dequantized second row, y column.</summary>
        public float Scale22 => 1f + M22 / Quantum;

        /// <summary>
        /// Quantizes a normalized linear part. Fails when an element falls outside the grid's
        /// range (about eight times the unit scale), which only extreme anisotropy reaches.
        /// </summary>
        public static bool TryQuantize(double m11, double m12, double m21, double m22,
            out GlyphMaskTransform transform)
        {
            transform = default;

            if (!TryQuantizeElement(m11 - 1, out var q11) || !TryQuantizeElement(m12, out var q12) ||
                !TryQuantizeElement(m21, out var q21) || !TryQuantizeElement(m22 - 1, out var q22))
            {
                return false;
            }

            transform = new GlyphMaskTransform(q11, q12, q21, q22);
            return true;
        }

        private static bool TryQuantizeElement(double value, out short quantized)
        {
            var q = Math.Round(value * Quantum);

            if (!(q >= short.MinValue && q <= short.MaxValue))
            {
                quantized = 0;
                return false;
            }

            quantized = (short)q;
            return true;
        }
    }

    /// <summary>
    /// Cache identity of a rasterized glyph mask. Scale is quantized to 1/8 px-per-em steps so
    /// floating-point noise in transform math cannot mint spurious variants; the subpixel x
    /// phase is bucketed to quarter pixels. Upright draws ride baseline snapping and have no y
    /// phase; a rotated or skewed draw moves glyph origins off the pixel grid in both axes, so
    /// its masks carry a <see cref="Transform"/> and a quarter-pixel <see cref="PhaseY"/> too.
    /// Neither opacity nor foreground tint is part of the identity: opacity rides the draw
    /// call's own parameter and tint variants are a run-mask concern, so animating either never
    /// touches this cache. <see cref="EmboldenQ"/> carries the bold simulation's outset in 1/64
    /// device pixels: its strength follows the em size a run was laid out at, which the scale
    /// bucket alone does not determine once a transform scales the text. <see cref="EmboldenQ"/>
    /// and <see cref="Oblique"/> together are the simulation the mask is built with, upright or
    /// transformed, so the masks of a face and of its simulated variants share one cache.
    /// </summary>
    internal readonly record struct GlyphMaskKey(
        ushort Glyph, ushort ScaleQ, byte Phase, GlyphMaskMode Mode, bool GridFit = true, bool Strong = false,
        ushort EmboldenQ = 0, bool Oblique = false, GlyphMaskTransform Transform = default, byte PhaseY = 0)
    {
        /// <summary>Number of subpixel x-phase buckets.</summary>
        public const int PhaseCount = 4;

        /// <summary>Scale quantization steps per device pixel of em size.</summary>
        public const float ScaleQuantum = 8f;

        /// <summary>The quantized device pixels per em this mask was rasterized at.</summary>
        public float PixelsPerEm => ScaleQ / ScaleQuantum;

        /// <summary>The subpixel x offset this mask's coverage was sampled at.</summary>
        public float PhaseOffset => Phase * (1f / PhaseCount);

        /// <summary>The subpixel y offset this mask's coverage was sampled at.</summary>
        public float PhaseOffsetY => PhaseY * (1f / PhaseCount);

        /// <summary>The bold simulation's outset in device pixels, zero when not emboldened.</summary>
        public float EmboldenOutset => EmboldenQ * (1f / 64);

        /// <summary>The simulations applied to the fitted outline of this mask.</summary>
        public FontSimulations Simulations =>
            (EmboldenQ > 0 ? FontSimulations.Bold : FontSimulations.None) |
            (Oblique ? FontSimulations.Oblique : FontSimulations.None);

        /// <summary>
        /// Whether this mask is built by the transformed builder: any rotation, skew or
        /// anisotropic scale, or a vertical phase. Everything else is the upright mask the
        /// axis-aligned builder produces.
        /// </summary>
        public bool IsTransformed => !Transform.IsIdentity || PhaseY != 0;

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
