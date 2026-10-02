using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Blends a composed subpixel run mask straight into a raster surface in one pass. The mask
    /// is the portable pair of payloads, premultiplied BGRA per pixel: a Multiply payload holding
    /// the inverse corrected stripe coverage per channel with an opaque alpha, and a Plus payload
    /// holding the pre-tinted corrected coverage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is byte for byte what drawing the Multiply payload with the multiply blend mode
    /// and then the Plus payload with the plus blend mode produces on Skia's 8-bit raster
    /// pipeline. Per channel, with <c>m</c> and <c>p</c> the payload bytes, <c>d</c> the
    /// destination channel and <c>da</c> the destination alpha (and <c>m = 255</c> in alpha):
    /// </para>
    /// <code>
    /// multiplied = (m * (255 - da) + m * d + 255) &gt;&gt; 8
    /// result     = min(255, multiplied + p)
    /// </code>
    /// <para>
    /// The multiply blend is <c>s * (1 - da) + d * (1 - sa) + s * d</c> with an opaque source, and
    /// the pipeline divides by 255 as <c>(v + 255) &gt;&gt; 8</c>, which lands within one level of
    /// the rounded quotient <c>(v + 127) / 255</c>, in either direction, for every product of two
    /// bytes. A target whose <see cref="GlyphBlitTarget.Arithmetic"/> is
    /// <see cref="GlyphBlitArithmetic.Rounded"/> divides to nearest instead, as Skia's ARM64 code
    /// does. The plus blend saturates. Alpha always comes out opaque.
    /// </para>
    /// <para>
    /// Since <c>d &lt;= da</c> in premultiplied pixels, <c>m * (255 - da + d) + 255</c> stays below
    /// 65536, so the vector paths compute it in 16-bit lanes.
    /// </para>
    /// </remarks>
    internal static class LcdMaskBlitter
    {
        private static readonly Vector128<byte> s_alphaBroadcast =
            Vector128.Create((byte)6, 7, 6, 7, 6, 7, 6, 7, 14, 15, 14, 15, 14, 15, 14, 15);

        private static readonly Vector128<byte> s_swapRedBlue =
            Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

        /// <summary>
        /// Blends the payloads, <paramref name="width"/> x <paramref name="height"/> pixels in
        /// rows <paramref name="width"/> apart, with their top-left at (<paramref name="x"/>,
        /// <paramref name="y"/>) in device pixels, clipped to the target's clip.
        /// </summary>
        public static void Blend(in GlyphBlitTarget target, ReadOnlySpan<uint> multiply,
            ReadOnlySpan<uint> plus, int width, int height, int x, int y)
        {
            if (target.Arithmetic == GlyphBlitArithmetic.Rounded)
            {
                Blend<RoundedDivide>(target, multiply, plus, width, height, x, y);
            }
            else
            {
                Blend<PipelineDivide>(target, multiply, plus, width, height, x, y);
            }
        }

        private static unsafe void Blend<TDivide>(in GlyphBlitTarget target, ReadOnlySpan<uint> multiply,
            ReadOnlySpan<uint> plus, int width, int height, int x, int y)
            where TDivide : struct, IDivide255
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (multiply.Length < width * height || plus.Length < width * height)
            {
                throw new ArgumentException("Payloads must hold width * height pixels.");
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

            var path = GlyphMaskBlitter.Path;
            var swap = target.IsRgba;

            fixed (uint* m = multiply)
            fixed (uint* p = plus)
            {
                for (var row = y0; row < y1; row++)
                {
                    var offset = (row - y) * width + (x0 - x);
                    var destination = (uint*)((byte*)target.Pixels + (long)row * target.RowBytes) + x0;
                    var count = x1 - x0;
                    var done = 0;

                    if (path == GlyphBlitPath.Portable)
                    {
                        done = BlendRowPortable<TDivide>(m + offset, p + offset, destination, count, swap);
                    }

                    if (path == GlyphBlitPath.Avx2)
                    {
                        done = BlendRowAvx2<TDivide>(m + offset, p + offset, destination, count, swap);
                    }

                    if (path is GlyphBlitPath.Ssse3 or GlyphBlitPath.Avx2)
                    {
                        done += BlendRowSsse3<TDivide>(m + offset + done, p + offset + done, destination + done,
                            count - done, swap);
                    }

                    BlendRowScalar<TDivide>(m + offset + done, p + offset + done, destination + done,
                        count - done, swap);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint SwapRedBlue(uint bgra) => (bgra & 0xFF00FF00) | ((bgra >> 16) & 0xFF) | ((bgra & 0xFF) << 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BlendRowScalar<TDivide>(uint* multiply, uint* plus, uint* destination, int count,
            bool swap)
            where TDivide : struct, IDivide255
        {
            for (var i = 0; i < count; i++)
            {
                var m = multiply[i];
                var p = plus[i];

                if (swap)
                {
                    m = SwapRedBlue(m);
                    p = SwapRedBlue(p);
                }

                var pixel = destination[i];
                var inverseAlpha = 255 - (int)(pixel >> 24);
                var result = 0u;

                for (var shift = 0; shift < 32; shift += 8)
                {
                    var mc = (int)((m >> shift) & 0xFF);
                    var dc = (int)((pixel >> shift) & 0xFF);
                    var multiplied = TDivide.Divide(mc * (inverseAlpha + dc));
                    var sum = multiplied + (int)((p >> shift) & 0xFF);

                    result |= (uint)Math.Min(255, sum) << shift;
                }

                destination[i] = result;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowSsse3<TDivide>(uint* multiply, uint* plus, uint* destination, int count,
            bool swap)
            where TDivide : struct, IDivide255
        {
            var i = 0;

            for (; i + 4 <= count; i += 4)
            {
                var m = Sse2.LoadVector128((byte*)(multiply + i));
                var p = Sse2.LoadVector128((byte*)(plus + i));

                if (swap)
                {
                    m = Ssse3.Shuffle(m, s_swapRedBlue);
                    p = Ssse3.Shuffle(p, s_swapRedBlue);
                }

                var current = Sse2.LoadVector128((byte*)(destination + i));
                var currentLow = Sse2.UnpackLow(current, Vector128<byte>.Zero).AsUInt16();
                var currentHigh = Sse2.UnpackHigh(current, Vector128<byte>.Zero).AsUInt16();

                var factorLow = Vector128.Create((ushort)255) - Ssse3.Shuffle(currentLow.AsByte(), s_alphaBroadcast).AsUInt16() + currentLow;
                var factorHigh = Vector128.Create((ushort)255) - Ssse3.Shuffle(currentHigh.AsByte(), s_alphaBroadcast).AsUInt16() + currentHigh;

                var multipliedLow = TDivide.Divide(Sse2.UnpackLow(m, Vector128<byte>.Zero).AsUInt16() * factorLow);
                var multipliedHigh = TDivide.Divide(Sse2.UnpackHigh(m, Vector128<byte>.Zero).AsUInt16() * factorHigh);

                var multiplied = Sse2.PackUnsignedSaturate(multipliedLow.AsInt16(), multipliedHigh.AsInt16());

                Sse2.Store((byte*)(destination + i), Sse2.AddSaturate(multiplied, p));
            }

            return i;
        }

        /// <summary>
        /// The <see cref="BlendRowSsse3"/> of the portable path: widening instead of unpacking,
        /// and the saturating byte addition as <c>m + min(p, 255 - m)</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowPortable<TDivide>(uint* multiply, uint* plus, uint* destination, int count,
            bool swap)
            where TDivide : struct, IDivide255
        {
            var i = 0;

            for (; i + 4 <= count; i += 4)
            {
                var m = Vector128.Load((byte*)(multiply + i));
                var p = Vector128.Load((byte*)(plus + i));

                if (swap)
                {
                    m = Vector128.Shuffle(m, Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15));
                    p = Vector128.Shuffle(p, Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15));
                }

                var current = Vector128.Load((byte*)(destination + i));
                var currentLow = Vector128.WidenLower(current);
                var currentHigh = Vector128.WidenUpper(current);
                var alpha = Vector128.Create((ushort)3, 3, 3, 3, 7, 7, 7, 7);

                var factorLow = Vector128.Create((ushort)255) - Vector128.Shuffle(currentLow, alpha) + currentLow;
                var factorHigh = Vector128.Create((ushort)255) - Vector128.Shuffle(currentHigh, alpha) + currentHigh;

                var multipliedLow = TDivide.Divide(Vector128.WidenLower(m) * factorLow);
                var multipliedHigh = TDivide.Divide(Vector128.WidenUpper(m) * factorHigh);

                // Both halves stay at or below 255, so the narrowing truncation keeps every value.
                var multiplied = Vector128.Narrow(multipliedLow, multipliedHigh);

                (multiplied + Vector128.Min(p, ~multiplied)).Store((byte*)(destination + i));
            }

            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int BlendRowAvx2<TDivide>(uint* multiply, uint* plus, uint* destination, int count,
            bool swap)
            where TDivide : struct, IDivide255
        {
            var alphaMask = Vector256.Create(s_alphaBroadcast, s_alphaBroadcast);
            var swapMask = Vector256.Create(s_swapRedBlue, s_swapRedBlue);
            var i = 0;

            for (; i + 8 <= count; i += 8)
            {
                var m = Avx.LoadVector256((byte*)(multiply + i));
                var p = Avx.LoadVector256((byte*)(plus + i));

                if (swap)
                {
                    m = Avx2.Shuffle(m, swapMask);
                    p = Avx2.Shuffle(p, swapMask);
                }

                // The unpacks work per 128-bit half, so each half's payload bytes line up with
                // its destination pixels.
                var current = Avx.LoadVector256((byte*)(destination + i));
                var currentLow = Avx2.UnpackLow(current, Vector256<byte>.Zero).AsUInt16();
                var currentHigh = Avx2.UnpackHigh(current, Vector256<byte>.Zero).AsUInt16();

                var factorLow = Vector256.Create((ushort)255) - Avx2.Shuffle(currentLow.AsByte(), alphaMask).AsUInt16() + currentLow;
                var factorHigh = Vector256.Create((ushort)255) - Avx2.Shuffle(currentHigh.AsByte(), alphaMask).AsUInt16() + currentHigh;

                var multipliedLow = TDivide.Divide(Avx2.UnpackLow(m, Vector256<byte>.Zero).AsUInt16() * factorLow);
                var multipliedHigh = TDivide.Divide(Avx2.UnpackHigh(m, Vector256<byte>.Zero).AsUInt16() * factorHigh);

                var multiplied = Avx2.PackUnsignedSaturate(multipliedLow.AsInt16(), multipliedHigh.AsInt16());

                Avx.Store((byte*)(destination + i), Avx2.AddSaturate(multiplied, p));
            }

            return i;
        }
    
        /// <summary>
        /// The division by 255 of the multiply blend's product, a product of two bytes; every
        /// form stays below 65536, so the vector paths compute it in 16-bit lanes.
        /// </summary>
        private interface IDivide255
        {
            static abstract int Divide(int product);

            static abstract Vector128<ushort> Divide(Vector128<ushort> product);

            static abstract Vector256<ushort> Divide(Vector256<ushort> product);
        }

        /// <summary><c>(v + 255) &gt;&gt; 8</c>: the 8-bit raster pipeline's division.</summary>
        private readonly struct PipelineDivide : IDivide255
        {
            public static int Divide(int product) => (product + 255) >> 8;

            public static Vector128<ushort> Divide(Vector128<ushort> product)
                => (product + Vector128.Create((ushort)255)) >>> 8;

            public static Vector256<ushort> Divide(Vector256<ushort> product)
                => (product + Vector256.Create((ushort)255)) >>> 8;
        }

        /// <summary>
        /// <c>(v + 127) / 255</c> by shifts, as <c>(p + (p &gt;&gt; 8)) &gt;&gt; 8</c> with
        /// <c>p = v + 128</c>: the division of Skia's ARM64 raster pipeline.
        /// </summary>
        private readonly struct RoundedDivide : IDivide255
        {
            public static int Divide(int product)
            {
                var rounded = product + 128;

                return (rounded + (rounded >> 8)) >> 8;
            }

            public static Vector128<ushort> Divide(Vector128<ushort> product)
            {
                var rounded = product + Vector128.Create((ushort)128);

                return (rounded + (rounded >>> 8)) >>> 8;
            }

            public static Vector256<ushort> Divide(Vector256<ushort> product)
            {
                var rounded = product + Vector256.Create((ushort)128);

                return (rounded + (rounded >>> 8)) >>> 8;
            }
        }
    }
}
