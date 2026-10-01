using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        private static readonly Vector128<byte> s_coverageLow =
            Vector128.Create((byte)0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 1, 0x80, 1, 0x80, 1, 0x80, 1, 0x80);

        private static readonly Vector128<byte> s_coverageHigh =
            Vector128.Create((byte)2, 0x80, 2, 0x80, 2, 0x80, 2, 0x80, 3, 0x80, 3, 0x80, 3, 0x80, 3, 0x80);

        private static readonly Vector128<byte> s_alphaBroadcast =
            Vector128.Create((byte)6, 7, 6, 7, 6, 7, 6, 7, 14, 15, 14, 15, 14, 15, 14, 15);

        private static readonly byte[] s_identity = CreateIdentity();

        /// <summary>
        /// Which instruction set the blends use; tests lower it to cover every path.
        /// </summary>
        internal static GlyphBlitPath Path { get; set; } = DetectPath();

        /// <summary>
        /// Blends <paramref name="mask"/> with its top-left at (<paramref name="x"/>,
        /// <paramref name="y"/>) in device pixels, clipped to the target's clip.
        /// </summary>
        /// <param name="target">The surface to write.</param>
        /// <param name="mask">A single-channel coverage mask.</param>
        /// <param name="x">Device column of the mask's first column.</param>
        /// <param name="y">Device row of the mask's first row.</param>
        /// <param name="tintBgra">The premultiplied tint in B, G, R, A byte order.</param>
        /// <param name="table">The coverage correction, or <c>null</c> to blend raw coverage.</param>
        public static void Blend(in GlyphBlitTarget target, GlyphMask mask, int x, int y, uint tintBgra,
            byte[]? table)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            Blend(target, mask.Alpha, mask.Width, mask.Height, x, y, tintBgra, table);
        }

        /// <summary>
        /// Blends single-channel coverage of <paramref name="width"/> x <paramref name="height"/>
        /// pixels, rows <paramref name="width"/> bytes apart, like
        /// <see cref="Blend(in GlyphBlitTarget, GlyphMask, int, int, uint, byte[])"/>.
        /// </summary>
        public static unsafe void Blend(in GlyphBlitTarget target, ReadOnlySpan<byte> coverage, int width, int height,
            int x, int y, uint tintBgra, byte[]? table)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (coverage.Length < width * height)
            {
                throw new ArgumentException("Coverage must hold width * height bytes.", nameof(coverage));
            }

            var clip = target.Clip;
            var x0 = Math.Max(clip.X, x);
            var y0 = Math.Max(clip.Y, y);
            var x1 = Math.Min(clip.Right, x + width);
            var y1 = Math.Min(clip.Bottom, y + height);

            if (x0 >= x1 || y0 >= y1)
            {
                return;
            }

            // The blend is per channel, so an RGBA surface only needs the tint's R and B swapped.
            var tint = target.IsRgba
                ? (tintBgra & 0xFF00FF00) | ((tintBgra >> 16) & 0xFF) | ((tintBgra & 0xFF) << 16)
                : tintBgra;

            var path = Path;

            fixed (byte* alpha = coverage)
            fixed (byte* lookup = table ?? s_identity)
            {
                for (var row = y0; row < y1; row++)
                {
                    var source = alpha + (row - y) * width + (x0 - x);
                    var destination = (uint*)((byte*)target.Pixels + (long)row * target.RowBytes) + x0;
                    var count = x1 - x0;
                    var done = 0;

                    // Glyph rows are short, often under sixteen pixels, so the eight-pixel steps
                    // leave a tail the four-pixel steps take before the scalar loop does.
                    if (path == GlyphBlitPath.Avx2)
                    {
                        done = BlendRowAvx2(source, destination, count, tint, lookup);
                    }

                    if (path != GlyphBlitPath.Scalar)
                    {
                        done += BlendRowSsse3(source + done, destination + done, count - done, tint, lookup);
                    }

                    BlendRowScalar(source + done, destination + done, count - done, tint, lookup);
                }
            }
        }

        private static GlyphBlitPath DetectPath()
            => Avx2.IsSupported ? GlyphBlitPath.Avx2 : Ssse3.IsSupported ? GlyphBlitPath.Ssse3 : GlyphBlitPath.Scalar;

        private static byte[] CreateIdentity()
        {
            var table = new byte[256];

            for (var i = 0; i < table.Length; i++)
            {
                table[i] = (byte)i;
            }

            return table;
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BlendRowScalar(byte* source, uint* destination, int count, uint tint, byte* table)
        {
            int tintB = (byte)tint, tintG = (byte)(tint >> 8), tintR = (byte)(tint >> 16), tintA = (byte)(tint >> 24);

            for (var i = 0; i < count; i++)
            {
                int coverage = table[source[i]];

                if (coverage == 0)
                {
                    continue;
                }

                var pixel = destination[i];
                var alpha = Multiply(tintA, coverage);
                var inverse = 255 - alpha;

                var c0 = Multiply(tintB, coverage) + Multiply((int)(pixel & 0xFF), inverse);
                var c1 = Multiply(tintG, coverage) + Multiply((int)((pixel >> 8) & 0xFF), inverse);
                var c2 = Multiply(tintR, coverage) + Multiply((int)((pixel >> 16) & 0xFF), inverse);
                var c3 = alpha + Multiply((int)(pixel >> 24), inverse);

                destination[i] = (uint)c0 | ((uint)c1 << 8) | ((uint)c2 << 16) | ((uint)c3 << 24);
            }
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowSsse3(byte* source, uint* destination, int count, uint tint, byte* table)
        {
            var tintLanes = Sse2.UnpackLow(Vector128.Create(tint, tint, 0, 0).AsByte(), Vector128<byte>.Zero).AsUInt16();
            var opaque = tint >> 24 == 255;
            var i = 0;

            for (; i + 4 <= count; i += 4)
            {
                var raw = *(uint*)(source + i);

                if (raw == 0)
                {
                    continue;
                }

                var coverage = table[(byte)raw] | ((uint)table[(byte)(raw >> 8)] << 8) |
                               ((uint)table[(byte)(raw >> 16)] << 16) | ((uint)table[raw >> 24] << 24);

                if (coverage == 0)
                {
                    continue;
                }

                var pixels = destination + i;

                if (coverage == 0xFFFFFFFF && opaque)
                {
                    // Full coverage of an opaque tint replaces the pixel with the tint itself.
                    pixels[0] = pixels[1] = pixels[2] = pixels[3] = tint;
                    continue;
                }

                var coverageBytes = Vector128.CreateScalar(coverage).AsByte();
                var coverageLow = Ssse3.Shuffle(coverageBytes, s_coverageLow).AsUInt16();
                var coverageHigh = Ssse3.Shuffle(coverageBytes, s_coverageHigh).AsUInt16();

                var current = Sse2.LoadVector128((byte*)pixels);
                var currentLow = Sse2.UnpackLow(current, Vector128<byte>.Zero).AsUInt16();
                var currentHigh = Sse2.UnpackHigh(current, Vector128<byte>.Zero).AsUInt16();

                var sourceLow = Multiply(tintLanes, coverageLow);
                var sourceHigh = Multiply(tintLanes, coverageHigh);

                var inverseLow = Vector128.Create((ushort)255) - Ssse3.Shuffle(sourceLow.AsByte(), s_alphaBroadcast).AsUInt16();
                var inverseHigh = Vector128.Create((ushort)255) - Ssse3.Shuffle(sourceHigh.AsByte(), s_alphaBroadcast).AsUInt16();

                var resultLow = sourceLow + Multiply(currentLow, inverseLow);
                var resultHigh = sourceHigh + Multiply(currentHigh, inverseHigh);

                Sse2.Store((byte*)pixels, Sse2.PackUnsignedSaturate(resultLow.AsInt16(), resultHigh.AsInt16()));
            }

            return i;
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowAvx2(byte* source, uint* destination, int count, uint tint, byte* table)
        {
            var tintLanes = Avx2.UnpackLow(Vector256.Create(tint).AsByte(), Vector256<byte>.Zero).AsUInt16();
            var coverageLowMask = Vector256.Create(s_coverageLow, s_coverageLow);
            var coverageHighMask = Vector256.Create(s_coverageHigh, s_coverageHigh);
            var alphaMask = Vector256.Create(s_alphaBroadcast, s_alphaBroadcast);
            var opaque = tint >> 24 == 255;
            var i = 0;

            for (; i + 8 <= count; i += 8)
            {
                var raw = *(ulong*)(source + i);

                if (raw == 0)
                {
                    continue;
                }

                var first = table[(byte)raw] | ((uint)table[(byte)(raw >> 8)] << 8) |
                            ((uint)table[(byte)(raw >> 16)] << 16) | ((uint)table[(byte)(raw >> 24)] << 24);
                var second = table[(byte)(raw >> 32)] | ((uint)table[(byte)(raw >> 40)] << 8) |
                             ((uint)table[(byte)(raw >> 48)] << 16) | ((uint)table[(byte)(raw >> 56)] << 24);

                if ((first | second) == 0)
                {
                    continue;
                }

                var pixels = destination + i;

                if ((first & second) == 0xFFFFFFFF && opaque)
                {
                    Avx.Store(pixels, Vector256.Create(tint));
                    continue;
                }

                // Each 128-bit half holds four pixels, and the unpacks and shuffles below work
                // per half: the low unpack takes pixels 0-1 (and 4-5), the high one 2-3 (and
                // 6-7), so each half's coverage bytes sit where its pixels do.
                var coverageBytes = Vector256.Create(Vector128.CreateScalar(first), Vector128.CreateScalar(second)).AsByte();
                var coverageLow = Avx2.Shuffle(coverageBytes, coverageLowMask).AsUInt16();
                var coverageHigh = Avx2.Shuffle(coverageBytes, coverageHighMask).AsUInt16();

                var current = Avx.LoadVector256((byte*)pixels);
                var currentLow = Avx2.UnpackLow(current, Vector256<byte>.Zero).AsUInt16();
                var currentHigh = Avx2.UnpackHigh(current, Vector256<byte>.Zero).AsUInt16();

                var sourceLow = Multiply(tintLanes, coverageLow);
                var sourceHigh = Multiply(tintLanes, coverageHigh);

                var inverseLow = Vector256.Create((ushort)255) - Avx2.Shuffle(sourceLow.AsByte(), alphaMask).AsUInt16();
                var inverseHigh = Vector256.Create((ushort)255) - Avx2.Shuffle(sourceHigh.AsByte(), alphaMask).AsUInt16();

                var resultLow = sourceLow + Multiply(currentLow, inverseLow);
                var resultHigh = sourceHigh + Multiply(currentHigh, inverseHigh);

                Avx.Store((byte*)pixels, Avx2.PackUnsignedSaturate(resultLow.AsInt16(), resultHigh.AsInt16()));
            }

            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> Multiply(Vector128<ushort> a, Vector128<ushort> b)
        {
            var product = a * b + Vector128.Create((ushort)128);

            return (product + (product >>> 8)) >>> 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<ushort> Multiply(Vector256<ushort> a, Vector256<ushort> b)
        {
            var product = a * b + Vector256.Create((ushort)128);

            return (product + (product >>> 8)) >>> 8;
        }

        /// <summary>
        /// <c>a * b / 255</c> rounded to nearest by shifts, equal to <c>(a * b + 127) / 255</c>
        /// for every pair of bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Multiply(int a, int b)
        {
            var product = a * b + 128;

            return (product + (product >> 8)) >> 8;
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
