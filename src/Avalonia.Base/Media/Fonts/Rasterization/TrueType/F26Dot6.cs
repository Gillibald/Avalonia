using System;

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
