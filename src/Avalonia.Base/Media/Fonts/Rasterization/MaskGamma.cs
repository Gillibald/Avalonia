using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Coverage correction for monochrome text — the reason backend text reads crisp while
    /// naively blended coverage reads thin and soft. Blending happens in gamma-encoded device
    /// space, so linear coverage produces the wrong intermediate tones; like the platform text
    /// stacks (Skia's mask gamma, DirectWrite's ClearType tables), this precompensates each
    /// coverage value so the device-space blend lands on the tone a linear-light blend would
    /// have produced, plus a contrast boost that steepens the edge profile.
    /// </summary>
    /// <remarks>
    /// Tables are keyed by the text color's luminance (8 buckets, top 3 bits — the platform
    /// convention) and assume the destination is the opposite extreme, the same guess the
    /// platform tables make: the correction is exact for dark-on-light and light-on-dark text
    /// and tapers off in between. Color glyph layers must NOT go through this — the transform
    /// is non-linear, so abutting layers whose coverages sum to full would show seams.
    /// </remarks>
    internal static class MaskGamma
    {
        /// <summary>Contrast boost applied to source coverage; the platform-typical value.</summary>
        internal const double Contrast = 0.5;

        /// <summary>Gamma exponent approximating the sRGB transfer curve for both endpoints.</summary>
        internal const double Gamma = 2.2;

        private const int LuminanceBits = 3;
        private const int TableCount = 1 << LuminanceBits;

        private static readonly byte[][] s_tables = BuildTables();

        /// <summary>
        /// The 256-entry coverage table for text of the given (straight, unpremultiplied)
        /// color, selected by luminance bucket.
        /// </summary>
        public static byte[] GetTable(byte r, byte g, byte b) => s_tables[GetBucket(r, g, b)];

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

        /// <summary>
        /// The correction as analytic parameters for a shader implementation: the GPU LCD
        /// blender computes the identical curve per stripe channel instead of sampling the
        /// 8-bit table, keyed by the same luminance bucket.
        /// </summary>
        internal readonly record struct GammaShaderParameters(
            float Contrast, float LumSrc, float LumDst, float LinSrc, float LinDst, bool NearEqual);

        internal static GammaShaderParameters GetShaderParameters(byte r, byte g, byte b)
        {
            var src = ReplicateBucket(GetBucket(r, g, b)) / 255.0;
            var dst = 1.0 - src;
            var linSrc = Math.Pow(src, Gamma);
            var linDst = Math.Pow(dst, Gamma);

            return new GammaShaderParameters(
                (float)(Contrast * linDst), (float)src, (float)dst, (float)linSrc, (float)linDst,
                Math.Abs(src - dst) < 1.0 / 256.0);
        }

        // Replicate the bucket bits across the byte so bucket 0 keys pure black and the last
        // bucket pure white.
        private static int ReplicateBucket(int bucket) => (bucket << 5) | (bucket << 2) | (bucket >> 1);

        private static byte[][] BuildTables()
        {
            var tables = new byte[TableCount][];

            for (var i = 0; i < TableCount; i++)
            {
                tables[i] = BuildTable((byte)ReplicateBucket(i));
            }

            return tables;
        }

        private static byte[] BuildTable(byte srcLuminance)
        {
            var table = new byte[256];

            var src = srcLuminance / 255.0;
            var linSrc = Math.Pow(src, Gamma);

            // Assume the destination is the opposite extreme — dark text sits on light ground
            // and vice versa. The correction is what makes that blend come out linear-light.
            var dst = 1.0 - src;
            var linDst = Math.Pow(dst, Gamma);

            // The boost tapers off as the text color approaches white, matching the platform
            // behavior: light text needs thinning, not thickening.
            var adjustedContrast = Contrast * linDst;

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
                    var output = Math.Pow(linOut, 1.0 / Gamma);

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
