using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Per-luminance-bucket <see cref="SKColorFilter"/>s applying the <see cref="MaskGamma"/>
    /// coverage correction to the A8 mask draw: the table rides the alpha channel, where the
    /// mask carries coverage. Shared and never disposed; paints and composed filters take
    /// their own refs.
    /// </summary>
    /// <remarks>
    /// The A8 draw uses the table rather than a closed-form runtime colour filter: on the
    /// raster pipeline the table stage costs well under half of the runtime filter per mask, and a plain image
    /// draw evaluates the table's texture reads in uniform control flow, so their implicit
    /// derivatives are defined and the lookups return the table on the GPU too.
    /// </remarks>
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
