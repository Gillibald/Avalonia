using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// An immutable rasterized glyph coverage mask: 8-bit alpha, row-major, stride equal to
    /// <see cref="Width"/>. <see cref="Left"/>/<see cref="Top"/> place the mask's top-left
    /// relative to the glyph's snapped integer pen position, in device pixels (the subpixel
    /// phase is baked into the coverage, not the placement).
    /// </summary>
    /// <remarks>
    /// Payload rule matches the glyph cache family: immutable, non-disposable, handed out
    /// lock-free with unbounded lifetime — composed run masks copy from it, so evicting a mask
    /// can never invalidate anything already composed.
    /// </remarks>
    internal sealed class GlyphMask
    {
        /// <summary>The shared no-ink mask (whitespace, malformed, or degenerate glyphs).</summary>
        public static readonly GlyphMask Empty = new(Array.Empty<byte>(), 0, 0, 0, 0);

        public GlyphMask(byte[] alpha, int width, int height, int left, int top, int channels = 1)
        {
            if (alpha.Length != width * height * channels)
            {
                throw new ArgumentException("Alpha length must equal width * height * channels.", nameof(alpha));
            }

            Alpha = alpha;
            Width = width;
            Height = height;
            Left = left;
            Top = top;
            Channels = channels;
        }

        public byte[] Alpha { get; }

        /// <summary>
        /// Coverage channels per pixel: 1 for grayscale/aliased, 3 for subpixel (interleaved
        /// RGB stripe coverage; a BGR destination swaps at consumption, so the cache stays
        /// geometry-agnostic). Row stride is <see cref="Width"/> * Channels bytes.
        /// </summary>
        public int Channels { get; }

        public int Width { get; }

        public int Height { get; }

        public int Left { get; }

        public int Top { get; }

        public bool IsEmpty => Alpha.Length == 0;

        /// <summary>Eviction weight: the pixel bytes plus a small fixed object overhead.</summary>
        public int ByteCost => Alpha.Length + 48;
    }
}
