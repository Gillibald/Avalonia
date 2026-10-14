// This source file contains logic adapted to C# from the FreeType project
// (https://freetype.org), src/base/ftcalc.c, and is a modified version of the
// original FreeType code, not the original.
//
// Copyright (C) 1996-2026 by David Turner, Robert Wilhelm, and Werner Lemberg.
//
// Used under the FreeType Project License (FTL); see NOTICE.md in the
// repository root for the full license text and the required credit.

using System;
using System.Numerics;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// 26.6 and 16.16 fixed-point helpers for the TrueType interpreter. All arithmetic runs
    /// on 64-bit intermediates and truncates or rounds exactly the way FreeType's FT_MulDiv
    /// and FT_MulFix do, because hinted output must be bit-identical across platforms and
    /// any drift here moves every stem that passes through the engine.
    /// </summary>
    internal static class F26Dot6
    {
        public const int One = 64;

        /// <summary>Largest multiple of 64 not above the value.</summary>
        public static int Floor(int value) => value & ~63;

        /// <summary>Nearest multiple of 64 (ties round up), the FT_PIX_ROUND form.</summary>
        public static int Round(int value) => (int)((value + 32L) & ~63L);

        /// <summary>Smallest multiple of 64 not below the value.</summary>
        public static int Ceiling(int value) => (int)((value + 63L) & ~63L);

        /// <summary>Nearest multiple of <paramref name="pad"/> (a power of two), FT_PAD_ROUND.</summary>
        public static int PadRound(int value, int pad) => (int)((value + (long)(pad / 2)) & ~(long)(pad - 1));

        /// <summary>
        /// (a * b) / c with round-to-nearest and symmetric sign handling, the FT_MulDiv
        /// contract. The MUL opcode is MulDivRounded(a, b, 64).
        /// </summary>
        public static int MulDivRounded(int a, int b, int c)
        {
            var sign = 1L;
            long la = a, lb = b, lc = c;

            if (la < 0) { la = -la; sign = -sign; }
            if (lb < 0) { lb = -lb; sign = -sign; }
            if (lc < 0) { lc = -lc; sign = -sign; }

            var value = lc > 0 ? (la * lb + lc / 2) / lc : 0x7FFFFFFFL;

            return (int)(sign > 0 ? value : -value);
        }

        /// <summary>
        /// (a * b) / c truncating toward zero, the FT_MulDiv_No_Round contract. The DIV
        /// opcode is MulDivTruncated(a, 64, b).
        /// </summary>
        public static int MulDivTruncated(int a, int b, int c)
        {
            var sign = 1L;
            long la = a, lb = b, lc = c;

            if (la < 0) { la = -la; sign = -sign; }
            if (lb < 0) { lb = -lb; sign = -sign; }
            if (lc < 0) { lc = -lc; sign = -sign; }

            var value = lc > 0 ? la * lb / lc : 0x7FFFFFFFL;

            return (int)(sign > 0 ? value : -value);
        }

        /// <summary>
        /// (a &lt;&lt; 16) / b rounded to nearest with symmetric signs, the FT_DivFix contract: the
        /// 16.16 ratio of two values. A zero divisor yields 0x7FFFFFFF with the dividend's sign,
        /// and ratios beyond the 32-bit range saturate.
        /// </summary>
        public static int DivFix(int a, int b)
        {
            var sign = 1L;
            long la = a, lb = b;

            if (la < 0) { la = -la; sign = -sign; }
            if (lb < 0) { lb = -lb; sign = -sign; }

            var value = lb > 0 ? Math.Min(((la << 16) + (lb >> 1)) / lb, 0x7FFFFFFFL) : 0x7FFFFFFFL;

            return (int)(sign > 0 ? value : -value);
        }

        /// <summary>
        /// Scales (x, y) to a 16.16 unit vector the way FT_Vector_NormLen does: prenormalise by
        /// a power of two, then Newton iterations on the reciprocal length from a linear
        /// estimate, all in wrapping 32-bit integer arithmetic. The result can sit a few
        /// 16.16 units off the exact direction, which the TrueType vector instructions
        /// inherit. An axis-aligned input yields an exact 0x10000 component; (0, 0) is left
        /// unchanged.
        /// </summary>
        public static void NormalizeVector(ref int x, ref int y)
        {
            var signX = 1;
            var signY = 1;
            var ux = (uint)x;
            var uy = (uint)y;

            if (x < 0) { ux = unchecked((uint)-x); signX = -1; }
            if (y < 0) { uy = unchecked((uint)-y); signY = -1; }

            if (ux == 0)
            {
                if (uy > 0)
                {
                    y = signY * 0x10000;
                }

                return;
            }

            if (uy == 0)
            {
                x = signX * 0x10000;
                return;
            }

            // Prenormalise so the estimated length lands between 2/3 and 4/3 in 16.16.
            var length = ux > uy ? ux + (uy >> 1) : uy + (ux >> 1);
            var shift = BitOperations.LeadingZeroCount(length);

            shift -= 15 + (length >= (0xAAAAAAAAu >> shift) ? 1 : 0);

            if (shift > 0)
            {
                ux <<= shift;
                uy <<= shift;

                // Tiny vectors need the estimate again at the new magnitude.
                length = ux > uy ? ux + (uy >> 1) : uy + (ux >> 1);
            }
            else
            {
                ux >>= -shift;
                uy >>= -shift;
                length >>= -shift;
            }

            // Lower linear approximation of the reciprocal length minus one.
            var b = unchecked(0x10000 - (int)length);
            var sx = (int)ux;
            var sy = (int)uy;
            uint u, v;
            int z;

            unchecked
            {
                do
                {
                    u = (uint)(sx + ((sx * b) >> 16));
                    v = (uint)(sy + ((sy * b) >> 16));

                    // The squared length approaches 2^32; as a signed value it is the
                    // difference from 2^32 even when the sum wraps.
                    z = -(int)(u * u + v * v) / 0x200;
                    z = z * ((0x10000 + b) >> 8) / 0x10000;

                    b += z;
                }
                while (z > 0);
            }

            x = signX < 0 ? -(int)u : (int)u;
            y = signY < 0 ? -(int)v : (int)v;
        }

        /// <summary>
        /// a * scale for a 16.16 scale factor with FT_MulFix rounding (nearest, symmetric
        /// signs). Converts font units to 26.6 pixels when the scale maps them so.
        /// </summary>
        public static int MulFix(int a, int scale16Dot16)
        {
            var sign = 1L;
            long la = a, ls = scale16Dot16;

            if (la < 0) { la = -la; sign = -sign; }
            if (ls < 0) { ls = -ls; sign = -sign; }

            var value = (la * ls + 0x8000L) >> 16;

            return (int)(sign > 0 ? value : -value);
        }
    }
}
