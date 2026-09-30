using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Direct write access to a CPU raster surface for the glyph mask blitter: premultiplied
    /// 32-bit pixels in BGRA or RGBA byte order and the device clip rectangle, which must be
    /// the whole clip (a rectangular clip, no layer or opacity in between).
    /// </summary>
    internal readonly struct GlyphBlitTarget
    {
        public GlyphBlitTarget(IntPtr pixels, int rowBytes, int width, int height, PixelRect clip, bool isRgba)
        {
            Pixels = pixels;
            RowBytes = rowBytes;
            Width = width;
            Height = height;
            Clip = clip.Intersect(new PixelRect(0, 0, width, height));
            IsRgba = isRgba;
        }

        public IntPtr Pixels { get; }

        public int RowBytes { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The device clip, already confined to the surface.</summary>
        public PixelRect Clip { get; }

        /// <summary>Whether the bytes of a pixel are R, G, B, A rather than B, G, R, A.</summary>
        public bool IsRgba { get; }
    }

    /// <summary>
    /// Blends cached glyph masks straight into a raster surface, with the arithmetic of
    /// <see cref="RunMaskComposer.ComposeTinted"/>: the coverage table first, then a
    /// premultiplied tint scaled by coverage, source-over, every product rounded to nearest.
    /// Blending a run's masks in run order into a transparent surface therefore leaves exactly
    /// the pixels of the pre-tinted run mask they would have composed.
    /// </summary>
    /// <remarks>
    /// The vector paths process eight (AVX2) or four (SSSE3) pixels per step in 16-bit lanes;
    /// both round <c>a * b / 255</c> as <c>(v + (v &gt;&gt; 8)) &gt;&gt; 8</c> with
    /// <c>v = a * b + 128</c>, which equals the scalar <c>(a * b + 127) / 255</c> for every
    /// pair of bytes. Coverage runs through the table before the blend, a table lookup per
    /// byte, since the tables are not linear.
    /// </remarks>
    internal static class GlyphMaskBlitter
    {
        /// <summary>
        /// Which instruction set <see cref="Blend"/> uses; tests lower it to cover every path.
        /// </summary>
        internal static GlyphBlitPath Path { get; set; } = GlyphBlitPath.Scalar;

        /// <summary>
        /// Blends <paramref name="mask"/> with its top-left at (<paramref name="x"/>,
        /// <paramref name="y"/>) in device pixels, clipped to the target's clip.
        /// </summary>
        public static void Blend(in GlyphBlitTarget target, GlyphMask mask, int x, int y, uint tintBgra,
            byte[]? table)
        {
        }
    }

    /// <summary>The instruction set <see cref="GlyphMaskBlitter"/> blends with.</summary>
    internal enum GlyphBlitPath : byte
    {
        Scalar,
        Ssse3,
        Avx2,
    }
}
