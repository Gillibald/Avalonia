using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Per-luminance-bucket <see cref="SKColorFilter"/>s applying the <see cref="MaskGamma"/>
    /// coverage correction on the GPU paths: the table rides the alpha channel, where both the
    /// A8 mask draw and the Slug tier's premultiplied-white shader output carry coverage.
    /// Shared and never disposed — paints and composed filters take their own refs.
    /// </summary>
    internal static class MaskGammaFilters
    {
        private static readonly SKColorFilter?[] s_filters = new SKColorFilter?[MaskGamma.BucketCount];
        private static readonly byte[] s_identity = BuildIdentity();

        public static SKColorFilter Get(byte r, byte g, byte b)
        {
            var bucket = MaskGamma.GetBucket(r, g, b);

            // SkiaSharp's CreateTable rejects null per-channel tables, so RGB gets an identity.
            return s_filters[bucket] ??= SKColorFilter.CreateTable(
                MaskGamma.GetTable(bucket), s_identity, s_identity, s_identity);
        }

        private static byte[] BuildIdentity()
        {
            var table = new byte[256];

            for (var i = 0; i < 256; i++)
            {
                table[i] = (byte)i;
            }

            return table;
        }
    }
}
