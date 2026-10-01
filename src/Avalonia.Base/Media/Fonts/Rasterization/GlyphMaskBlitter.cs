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
    /// <para>
    /// The tint scaled by the corrected coverage depends on the coverage byte alone, so a blend
    /// looks it up in a 256-entry table of premultiplied source pixels made once per tint and
    /// coverage table: the AVX2 path gathers eight at a time. Only the destination's share,
    /// scaled by the source's inverse alpha, is computed per pixel.
    /// </para>
    /// <para>
    /// The vector paths process eight (AVX2) or four (SSSE3) pixels per step in 16-bit lanes;
    /// both round <c>a * b / 255</c> as <c>(v + (v &gt;&gt; 8)) &gt;&gt; 8</c> with
    /// <c>v = a * b + 128</c>, which equals the scalar <c>(a * b + 127) / 255</c> for every
    /// pair of bytes.
    /// </para>
    /// </remarks>
    internal static class GlyphMaskBlitter
    {
        private const int SourceTableCount = 8;

        private static readonly Vector128<byte> s_alphaBroadcast =
            Vector128.Create((byte)6, 7, 6, 7, 6, 7, 6, 7, 14, 15, 14, 15, 14, 15, 14, 15);

        private static readonly byte[] s_identity = CreateIdentity();

        [ThreadStatic]
        private static SourceTables? t_sources;

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
        // Compiled optimized at once rather than tiered: a profile gathered from the first masks
        // drawn (mostly empty and solid spans, or mostly mixed ones) lays the loop out for those,
        // and costs later masks of the other kind up to a third of their time.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
            var sources = GetSourceTable(tint, table ?? s_identity);

            // Where full coverage stays full, a fully covered pixel of an opaque tint becomes the
            // tint itself, so solid spans are stored without a lookup.
            var fill = tint >> 24 == 255 && sources[255] == tint;

            fixed (byte* alpha = coverage)
            fixed (uint* sourcePointer = sources)
            {
                var fillLanes = Vector256.Create(tint);

                for (var row = y0; row < y1; row++)
                {
                    var source = alpha + (row - y) * width + (x0 - x);
                    var destination = (uint*)((byte*)target.Pixels + (long)row * target.RowBytes) + x0;
                    var count = x1 - x0;
                    var done = 0;

                    // Glyph rows are short, often under sixteen pixels: a row at least one step
                    // wide ends with an overlapping step, and only narrower rows reach the
                    // smaller steps or the scalar loop.
                    if (path == GlyphBlitPath.Avx2)
                    {
                        done = BlendRowAvx2(source, destination, count, sourcePointer, fill, fillLanes);
                    }

                    if (path != GlyphBlitPath.Scalar)
                    {
                        done += BlendRowSsse3(source + done, destination + done, count - done, sourcePointer, fill,
                            fillLanes.GetLower());
                    }

                    BlendRowScalar(source + done, destination + done, count - done, sourcePointer);
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

        /// <summary>
        /// The premultiplied source pixel of every coverage byte: the tint scaled by the
        /// corrected coverage. A few recent tables are kept per thread, since the glyphs of a
        /// frame and of COLR layers alternate between a handful of tints.
        /// </summary>
        private static uint[] GetSourceTable(uint tint, byte[] table)
        {
            var cache = t_sources ??= new SourceTables();

            for (var i = 0; i < SourceTableCount; i++)
            {
                if (cache.Tints[i] == tint && ReferenceEquals(cache.Coverage[i], table))
                {
                    return cache.Sources[i];
                }
            }

            var slot = cache.Next;
            var sources = cache.Sources[slot] ??= new uint[256];
            int tintB = (byte)tint, tintG = (byte)(tint >> 8), tintR = (byte)(tint >> 16), tintA = (byte)(tint >> 24);

            for (var i = 0; i < 256; i++)
            {
                int coverage = table[i];

                sources[i] = (uint)Multiply(tintB, coverage) | ((uint)Multiply(tintG, coverage) << 8) |
                             ((uint)Multiply(tintR, coverage) << 16) | ((uint)Multiply(tintA, coverage) << 24);
            }

            cache.Tints[slot] = tint;
            cache.Coverage[slot] = table;
            cache.Next = (slot + 1) % SourceTableCount;

            return sources;
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BlendRowScalar(byte* source, uint* destination, int count, uint* sources)
        {
            for (var i = 0; i < count; i++)
            {
                var pixel = sources[source[i]];

                if (pixel == 0)
                {
                    continue;
                }

                var current = destination[i];
                var inverse = 255 - (int)(pixel >> 24);

                var c0 = (int)(pixel & 0xFF) + Multiply((int)(current & 0xFF), inverse);
                var c1 = (int)((pixel >> 8) & 0xFF) + Multiply((int)((current >> 8) & 0xFF), inverse);
                var c2 = (int)((pixel >> 16) & 0xFF) + Multiply((int)((current >> 16) & 0xFF), inverse);
                var c3 = (int)(pixel >> 24) + Multiply((int)(current >> 24), inverse);

                destination[i] = (uint)c0 | ((uint)c1 << 8) | ((uint)c2 << 16) | ((uint)c3 << 24);
            }
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowSsse3(byte* source, uint* destination, int count, uint* sources, bool fill,
            Vector128<uint> fillLanes)
        {
            var i = 0;

            for (; i + 4 <= count; i += 4)
            {
                BlendFourSsse3(*(uint*)(source + i), destination + i, sources, fill, fillLanes);
            }

            // The last pixels of a row at least four wide blend as the last four with the
            // coverage of those already blended masked to zero, which leaves them as they are.
            var rest = count - i;

            if (rest > 0 && count >= 4)
            {
                BlendFourSsse3(*(uint*)(source + count - 4) & (uint.MaxValue << (8 * (4 - rest))),
                    destination + count - 4, sources, fill, fillLanes);
                i = count;
            }

            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BlendFourSsse3(uint raw, uint* pixels, uint* sources, bool fill,
            Vector128<uint> fillLanes)
        {
            if (raw == 0)
            {
                return;
            }

            if (raw == 0xFFFFFFFF && fill)
            {
                Sse2.Store(pixels, fillLanes);
                return;
            }

            var source = Vector128.Create(sources[(byte)raw], sources[(byte)(raw >> 8)], sources[(byte)(raw >> 16)],
                sources[raw >> 24]).AsByte();

            var current = Sse2.LoadVector128((byte*)pixels);
            var currentLow = Sse2.UnpackLow(current, Vector128<byte>.Zero).AsUInt16();
            var currentHigh = Sse2.UnpackHigh(current, Vector128<byte>.Zero).AsUInt16();
            var sourceLow = Sse2.UnpackLow(source, Vector128<byte>.Zero).AsUInt16();
            var sourceHigh = Sse2.UnpackHigh(source, Vector128<byte>.Zero).AsUInt16();

            var inverseLow = Vector128.Create((ushort)255) - Ssse3.Shuffle(sourceLow.AsByte(), s_alphaBroadcast).AsUInt16();
            var inverseHigh = Vector128.Create((ushort)255) - Ssse3.Shuffle(sourceHigh.AsByte(), s_alphaBroadcast).AsUInt16();

            var resultLow = sourceLow + Multiply(currentLow, inverseLow);
            var resultHigh = sourceHigh + Multiply(currentHigh, inverseHigh);

            Sse2.Store((byte*)pixels, Sse2.PackUnsignedSaturate(resultLow.AsInt16(), resultHigh.AsInt16()));
        }

        // Inlined into the row loop: a call per glyph row costs as much as blending it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowAvx2(byte* source, uint* destination, int count, uint* sources, bool fill,
            Vector256<uint> fillLanes)
        {
            var alphaMask = Vector256.Create(s_alphaBroadcast, s_alphaBroadcast);
            var i = 0;

            // Large masks are mostly empty or solid: test 32 pixels of coverage at a time, and
            // blend only the spans that mix.
            for (; i + 32 <= count; i += 32)
            {
                var raw = Avx.LoadVector256(source + i);

                if (Avx.TestZ(raw, raw))
                {
                    continue;
                }

                if (fill && Avx2.MoveMask(Avx2.CompareEqual(raw, Vector256<byte>.AllBitsSet)) == -1)
                {
                    var pixels = destination + i;

                    Avx.Store(pixels, fillLanes);
                    Avx.Store(pixels + 8, fillLanes);
                    Avx.Store(pixels + 16, fillLanes);
                    Avx.Store(pixels + 24, fillLanes);
                    continue;
                }

                for (var j = i; j < i + 32; j += 8)
                {
                    BlendEightAvx2(*(ulong*)(source + j), destination + j, sources, fill, fillLanes, alphaMask);
                }
            }

            for (; i + 8 <= count; i += 8)
            {
                BlendEightAvx2(*(ulong*)(source + i), destination + i, sources, fill, fillLanes, alphaMask);
            }

            // The last pixels of a row at least eight wide blend as the last eight with the
            // coverage of those already blended masked to zero, which leaves them as they are.
            var rest = count - i;

            if (rest > 0 && count >= 8)
            {
                BlendEightAvx2(*(ulong*)(source + count - 8) & (ulong.MaxValue << (8 * (8 - rest))),
                    destination + count - 8, sources, fill, fillLanes, alphaMask);
                i = count;
            }

            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BlendEightAvx2(ulong raw, uint* pixels, uint* sources, bool fill,
            Vector256<uint> fillLanes, Vector256<byte> alphaMask)
        {
            if (raw == 0)
            {
                return;
            }

            if (raw == ulong.MaxValue && fill)
            {
                Avx.Store(pixels, fillLanes);
                return;
            }

            var indices = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(raw).AsByte());
            var source = Avx2.GatherVector256((int*)sources, indices, 4).AsByte();

            // Each 128-bit half holds four pixels, and the unpacks and shuffles below work per
            // half: the low unpack takes pixels 0-1 (and 4-5), the high one 2-3 (and 6-7).
            var current = Avx.LoadVector256((byte*)pixels);
            var currentLow = Avx2.UnpackLow(current, Vector256<byte>.Zero).AsUInt16();
            var currentHigh = Avx2.UnpackHigh(current, Vector256<byte>.Zero).AsUInt16();
            var sourceLow = Avx2.UnpackLow(source, Vector256<byte>.Zero).AsUInt16();
            var sourceHigh = Avx2.UnpackHigh(source, Vector256<byte>.Zero).AsUInt16();

            var inverseLow = Vector256.Create((ushort)255) - Avx2.Shuffle(sourceLow.AsByte(), alphaMask).AsUInt16();
            var inverseHigh = Vector256.Create((ushort)255) - Avx2.Shuffle(sourceHigh.AsByte(), alphaMask).AsUInt16();

            var resultLow = sourceLow + Multiply(currentLow, inverseLow);
            var resultHigh = sourceHigh + Multiply(currentHigh, inverseHigh);

            Avx.Store((byte*)pixels, Avx2.PackUnsignedSaturate(resultLow.AsInt16(), resultHigh.AsInt16()));
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

        /// <summary>The per-thread source tables of the most recent tints and coverage tables.</summary>
        private sealed class SourceTables
        {
            public readonly uint[][] Sources = new uint[SourceTableCount][];
            public readonly uint[] Tints = new uint[SourceTableCount];
            public readonly byte[]?[] Coverage = new byte[SourceTableCount][];
            public int Next;
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
