using System;
using System.Threading;

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

        private const ulong InkMeasured = 1UL << 63;

        // The rectangle of rows and columns with any coverage, packed 16 bits per value under
        // InkMeasured; zero until first asked for. Every thread that measures gets the same value,
        // so a race only measures twice.
        private long _ink;

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

        private GlyphMask(byte[] buffer, int width, int height, int left, int top)
        {
            Alpha = buffer;
            Width = width;
            Height = height;
            Left = left;
            Top = top;
            Channels = 1;
        }

        /// <summary>
        /// Wraps a single-channel mask over a buffer that may be longer than the mask, such as
        /// one rented from a pool. Rows are read by <see cref="Width"/> stride, so the unused
        /// tail is never touched. Such a mask is transient: it must not enter a cache, since the
        /// buffer goes back to its owner once the mask has been composed.
        /// </summary>
        internal static GlyphMask CreateOverBuffer(byte[] buffer, int width, int height, int left, int top)
        {
            if (width <= 0 || height <= 0 || buffer.Length < width * height)
            {
                throw new ArgumentException("The buffer must hold width * height bytes.", nameof(buffer));
            }

            return new GlyphMask(buffer, width, height, left, top);
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

        /// <summary>
        /// The smallest rectangle of the mask holding all its coverage, in mask pixels; zero
        /// width and height when no pixel is covered. The placement of a transformed glyph mask
        /// bounds the transformed corners of the glyph's ink box, which leaves a third or more
        /// of the mask uncovered under rotation, so a blend that skips pixels without coverage
        /// saves that much by blending this rectangle alone. Measured on first use and kept.
        /// A multi-channel mask reports its full extent.
        /// </summary>
        public void GetInkBounds(out int x, out int y, out int width, out int height)
        {
            var ink = (ulong)Volatile.Read(ref _ink);

            if (ink == 0)
            {
                ink = MeasureInk();
                Volatile.Write(ref _ink, (long)ink);
            }

            x = (int)(ink & 0xFFFF);
            y = (int)((ink >> 16) & 0xFFFF);
            width = (int)((ink >> 32) & 0xFFFF);
            height = (int)((ink >> 48) & 0x7FFF);
        }

        private ulong MeasureInk()
        {
            if (Channels != 1 || Width > 0x7FFF || Height > 0x7FFF)
            {
                return Pack(0, 0, Width, Height);
            }

            var alpha = Alpha.AsSpan(0, Width * Height);
            var top = 0;

            while (top < Height && !alpha.Slice(top * Width, Width).ContainsAnyExcept((byte)0))
            {
                top++;
            }

            if (top == Height)
            {
                return Pack(0, 0, 0, 0);
            }

            var bottom = Height - 1;

            while (!alpha.Slice(bottom * Width, Width).ContainsAnyExcept((byte)0))
            {
                bottom--;
            }

            var left = Width;
            var right = -1;

            for (var row = top; row <= bottom; row++)
            {
                var line = alpha.Slice(row * Width, Width);
                var first = line.IndexOfAnyExcept((byte)0);

                if (first < 0)
                {
                    continue;
                }

                left = Math.Min(left, first);
                right = Math.Max(right, line.LastIndexOfAnyExcept((byte)0));
            }

            return Pack(left, top, right - left + 1, bottom - top + 1);

            static ulong Pack(int x, int y, int width, int height)
                => InkMeasured | (uint)x | ((ulong)(uint)y << 16) | ((ulong)(uint)width << 32) |
                   ((ulong)(uint)height << 48);
        }
    }
}
