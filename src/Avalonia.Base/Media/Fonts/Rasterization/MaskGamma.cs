using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Coverage correction for monochrome text. Blending happens in gamma-encoded device space,
    /// where the platform text stacks do not blend raw coverage linearly either: each coverage
    /// value is mapped through a per-luminance table before the device-space blit, so text
    /// carries the weight the platform gives it.
    /// </summary>
    /// <remarks>
    /// Tables are keyed by the text color's luminance (8 buckets, top 3 bits, the platform
    /// convention). Grayscale tables reproduce DirectWrite's grayscale blend per bucket; the LCD
    /// tables are a separate family built with Skia's mask-gamma model, which assumes the
    /// destination is the opposite extreme of the text color. Color glyph layers must NOT go
    /// through this: the transform is non-linear, so abutting layers whose coverages sum to
    /// full would show seams.
    /// </remarks>
    internal static class MaskGamma
    {
        /// <summary>
        /// Grayscale transfer per luminance bucket: coverage c maps to c + c(1 - c)(A + B c),
        /// least-squares fitted to the effective alpha that DirectWrite's grayscale blend gives
        /// raw coverage (default rendering parameters: gamma 1.8, grayscale enhanced contrast
        /// 1.0) for gray text at the bucket's luminance, over 5 fonts at 9-16 px; every bucket
        /// within RMS 3.4/255 of the measurement. Black text blends almost linearly; lighter
        /// text comes out heavier than its raw coverage, white text most. The form keeps 0 and
        /// 1 fixed, and with these coefficients its slope stays above 0.5 on [0, 1], so every
        /// table is monotonic.
        /// </summary>
        private static readonly (double A, double B)[] s_grayscaleTransfer =
        {
            (-0.040, -0.158), (0.315, -0.449), (0.613, -0.642), (0.978, -0.956),
            (1.093, -1.049), (0.900, -0.914), (0.928, -0.776), (1.127, -0.734),
        };

        /// <summary>
        /// The LCD channels take their own family: subpixel coverage already triples effective
        /// edge resolution, and a strong boost hardens stems past the platform look and
        /// saturates the fringes. Values picked by the LCD_GAMMA_CALIBRATION probe:
        /// per-candidate RMSE against the DirectWrite-host LCD blob at identical glyphs,
        /// pens and hinting, measured optimum 1.6/0.20 (aggregate RMSE 88.4 across
        /// 11-24 px vs 102.0 for 2.2/0.50 and 111.9 for no correction).
        /// </summary>
        internal const double LcdContrast = 0.2;
        internal const double LcdGamma = 1.6;

        private const int LuminanceBits = 3;
        private const int TableCount = 1 << LuminanceBits;

        private static readonly byte[][] s_tables = BuildGrayscaleTables();
        private static readonly byte[][] s_lcdTables = BuildTables(LcdContrast, LcdGamma);

        /// <summary>
        /// The 256-entry coverage table for text of the given (straight, unpremultiplied)
        /// color, selected by luminance bucket.
        /// </summary>
        public static byte[] GetTable(byte r, byte g, byte b) => s_tables[GetBucket(r, g, b)];

        /// <summary>The LCD-strength table for a straight RGB tint.</summary>
        public static byte[] GetLcdTable(byte r, byte g, byte b) => s_lcdTables[GetBucket(r, g, b)];

        /// <summary>The number of luminance buckets (for callers caching per-bucket state).</summary>
        public static int BucketCount => TableCount;

        /// <summary>The luminance bucket for a straight color; pairs with <see cref="GetTable(int)"/>.</summary>
        public static int GetBucket(byte r, byte g, byte b)
        {
            // Rec. 709 luma on the gamma-encoded bytes — the same cheap keying the platform
            // stacks use for table selection.
            var luminance = (54 * r + 183 * g + 19 * b) >> 8;

            return luminance >> (8 - LuminanceBits);
        }

        /// <summary>The 256-entry coverage table for a bucket from <see cref="GetBucket"/>.</summary>
        public static byte[] GetTable(int bucket) => s_tables[bucket];

        /// <summary>
        /// Table lookup for a premultiplied BGRA tint: un-premultiplies for bucket selection so
        /// a translucent foreground still keys on its actual color.
        /// </summary>
        public static byte[] GetTableForPremulBgra(uint tintBgra)
        {
            var a = (byte)(tintBgra >> 24);

            if (a == 0)
            {
                return s_tables[0];
            }

            var b = (byte)Math.Min(255, (tintBgra & 0xFF) * 255 / a);
            var g = (byte)Math.Min(255, ((tintBgra >> 8) & 0xFF) * 255 / a);
            var r = (byte)Math.Min(255, ((tintBgra >> 16) & 0xFF) * 255 / a);

            return GetTable(r, g, b);
        }

        /// <summary>The LCD-strength table for a premultiplied BGRA tint.</summary>
        public static byte[] GetLcdTableForPremulBgra(uint tintBgra)
        {
            var a = (byte)(tintBgra >> 24);

            if (a == 0)
            {
                return s_lcdTables[0];
            }

            var b = (byte)Math.Min(255, (tintBgra & 0xFF) * 255 / a);
            var g = (byte)Math.Min(255, ((tintBgra >> 8) & 0xFF) * 255 / a);
            var r = (byte)Math.Min(255, ((tintBgra >> 16) & 0xFF) * 255 / a);

            return GetLcdTable(r, g, b);
        }

        /// <summary>A one-off table with explicit parameters — the calibration probe's hook,
        /// not a pipeline path.</summary>
        internal static byte[] BuildCalibrationTable(byte srcLuminance, double contrast, double gamma)
            => BuildTable(srcLuminance, contrast, gamma);

        /// <summary>
        /// The correction as analytic parameters for a shader implementation: the GPU LCD
        /// blender computes the identical curve per stripe channel instead of sampling the
        /// 8-bit table, keyed by the same luminance bucket.
        /// </summary>
        internal readonly record struct GammaShaderParameters(
            float Contrast, float LumSrc, float LumDst, float LinSrc, float LinDst, bool NearEqual,
            float InverseGamma);

        /// <summary>The LCD-strength parameters for the GPU blender.</summary>
        internal static GammaShaderParameters GetLcdShaderParameters(byte r, byte g, byte b)
            => GetShaderParameters(r, g, b, LcdContrast, LcdGamma);

        private static GammaShaderParameters GetShaderParameters(byte r, byte g, byte b,
            double contrast, double gamma)
        {
            var src = ReplicateBucket(GetBucket(r, g, b)) / 255.0;
            var dst = 1.0 - src;
            var linSrc = Math.Pow(src, gamma);
            var linDst = Math.Pow(dst, gamma);

            return new GammaShaderParameters(
                (float)(contrast * linDst), (float)src, (float)dst, (float)linSrc, (float)linDst,
                Math.Abs(src - dst) < 1.0 / 256.0, (float)(1.0 / gamma));
        }

        // Replicate the bucket bits across the byte so bucket 0 keys pure black and the last
        // bucket pure white.
        private static int ReplicateBucket(int bucket) => (bucket << 5) | (bucket << 2) | (bucket >> 1);

        private static byte[][] BuildGrayscaleTables()
        {
            var tables = new byte[TableCount][];

            for (var i = 0; i < TableCount; i++)
            {
                var (a, b) = s_grayscaleTransfer[i];
                var table = new byte[256];

                for (var j = 0; j < 256; j++)
                {
                    var coverage = j / 255.0;
                    var result = coverage + coverage * (1.0 - coverage) * (a + b * coverage);

                    table[j] = (byte)Math.Clamp((int)Math.Round(255.0 * result), 0, 255);
                }

                tables[i] = table;
            }

            return tables;
        }

        private static byte[][] BuildTables(double contrast, double gamma)
        {
            var tables = new byte[TableCount][];

            for (var i = 0; i < TableCount; i++)
            {
                tables[i] = BuildTable((byte)ReplicateBucket(i), contrast, gamma);
            }

            return tables;
        }

        private static byte[] BuildTable(byte srcLuminance, double contrast, double gamma)
        {
            var table = new byte[256];

            var src = srcLuminance / 255.0;
            var linSrc = Math.Pow(src, gamma);

            // Assume the destination is the opposite extreme — dark text sits on light ground
            // and vice versa. The correction is what makes that blend come out linear-light.
            var dst = 1.0 - src;
            var linDst = Math.Pow(dst, gamma);

            // The boost tapers off as the text color approaches white, matching the platform
            // behavior: light text needs thinning, not thickening.
            var adjustedContrast = contrast * linDst;

            var nearEqual = Math.Abs(src - dst) < 1.0 / 256.0;

            for (var i = 0; i < 256; i++)
            {
                var coverage = i / 255.0;
                var boosted = ApplyContrast(coverage, adjustedContrast);

                double result;

                if (nearEqual)
                {
                    // Blending mid-gray onto mid-gray: the gamma solve below divides by zero,
                    // and no correction is meaningful — keep only the contrast shape.
                    result = boosted;
                }
                else
                {
                    // The tone a linear-light blend would produce, re-encoded to device space,
                    // then solved back to the coverage the device-space blit must be given.
                    var linOut = linSrc * boosted + (1.0 - boosted) * linDst;
                    var output = Math.Pow(linOut, 1.0 / gamma);

                    result = (output - dst) / (src - dst);
                }

                table[i] = (byte)Math.Clamp((int)Math.Round(255.0 * result), 0, 255);
            }

            // Coverage endpoints are load-bearing: nothing may leak ink at zero coverage, and
            // full coverage must stay fully opaque.
            table[0] = 0;
            table[255] = 255;

            return table;
        }

        private static double ApplyContrast(double coverage, double contrast)
            => coverage + (1.0 - coverage) * contrast * coverage;
    }
}
