using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class MaskGammaTests
    {
        [Fact]
        public void Endpoints_Are_Exact_For_Every_Bucket()
        {
            for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
            {
                var table = MaskGamma.GetTable(bucket);

                // Zero coverage must never leak ink; full coverage must stay fully opaque.
                Assert.Equal(0, table[0]);
                Assert.Equal(255, table[255]);
            }
        }

        [Fact]
        public void Tables_Are_Monotonic()
        {
            for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
            {
                var table = MaskGamma.GetTable(bucket);

                for (var i = 1; i < 256; i++)
                {
                    Assert.True(table[i] >= table[i - 1],
                        $"bucket {bucket}: table[{i}] = {table[i]} < table[{i - 1}] = {table[i - 1]}");
                }
            }
        }

        [Fact]
        public void Dark_And_Light_Text_Curve_In_Opposite_Directions()
        {
            var black = MaskGamma.GetTable(0, 0, 0);
            var white = MaskGamma.GetTable(255, 255, 255);

            // Both corrections lighten the blend result relative to naive device-space
            // coverage: dark-on-light edges come out below identity (the naive blend was too
            // dark), light-on-dark edges above it — and the two curves must differ.
            Assert.True(black[128] < 128, $"black table at half coverage: {black[128]}");
            Assert.True(white[128] > 128, $"white table at half coverage: {white[128]}");
            Assert.NotEqual(black[64], white[64]);
        }

        [Fact]
        public void Premultiplied_Lookup_Matches_The_Straight_Color()
        {
            // Half-alpha premultiplied red (A 0x80, R premultiplied to 0x80) must key the same
            // bucket as straight red, not the darker premultiplied bytes.
            var straight = MaskGamma.GetTable(0xFF, 0x00, 0x00);
            var premul = MaskGamma.GetTableForPremulBgra(0x80800000u);

            Assert.Same(straight, premul);
        }
    }
}
