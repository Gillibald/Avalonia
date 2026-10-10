namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Identifies the colour mask of a COLR v1 glyph: its paint graph rasterized into
    /// premultiplied BGRA at <see cref="ScaleQ"/> (the scale bucket of <see cref="GlyphMaskKey"/>)
    /// and horizontal subpixel <see cref="Phase"/>, resolved with <see cref="Palette"/> and, for a
    /// paint that uses the CPAL foreground sentinel, with <see cref="Foreground"/>. A mask drawn
    /// under a rotation, skew or anisotropic scale also carries the quantized linear part of the
    /// device transform (<see cref="Transform"/>, as <see cref="GlyphMaskKey.Transform"/>) and a
    /// vertical phase.
    /// </summary>
    /// <remarks>
    /// Colour glyphs are never hinted, simulated or rendered with subpixel coverage, so mode, grid
    /// fit and simulation are not part of the key.
    /// </remarks>
    internal readonly record struct ColorGlyphMaskKey(
        ushort Glyph, ushort ScaleQ, byte Phase, ushort Palette = 0, uint Foreground = 0, bool HasForeground = false,
        GlyphMaskTransform Transform = default, byte PhaseY = 0)
    {
        /// <summary>Whether the mask is drawn under a transform other than an upright scale.</summary>
        public bool IsTransformed => !Transform.IsIdentity || PhaseY != 0;

        /// <summary>The subpixel y offset this mask was rasterized at.</summary>
        public float PhaseOffsetY => PhaseY * (1f / GlyphMaskKey.PhaseCount);

        /// <summary>The quantized device pixels per em this mask was rasterized at.</summary>
        public float PixelsPerEm => ScaleQ / GlyphMaskKey.ScaleQuantum;

        /// <summary>The subpixel x offset this mask was rasterized at.</summary>
        public float PhaseOffset => Phase * (1f / GlyphMaskKey.PhaseCount);
    }
}
