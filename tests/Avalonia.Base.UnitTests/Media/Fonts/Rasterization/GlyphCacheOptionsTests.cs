using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    /// <summary>
    /// The glyph cache limit comes from <see cref="FontManagerOptions.GlyphCacheLimitBytes"/>, or
    /// from the platform when the application sets none, and a change applies at the next frame.
    /// </summary>
    public class GlyphCacheOptionsTests
    {
        private const long Mb = 1024 * 1024;

        [Fact]
        public void The_Limit_Set_On_FontManagerOptions_Applies_At_The_Next_Frame()
        {
            var options = new FontManagerOptions { GlyphCacheLimitBytes = 8 * Mb };
            var budget = new GlyphCacheBudget(() => options, TextRasterizationPlatform.Windows);

            using (budget.BeginFrame())
            {
                Assert.Equal(8 * Mb, budget.LimitBytes);
                Assert.Equal(4 * Mb, budget.RetainBytes);
            }

            options.GlyphCacheLimitBytes = 100 * Mb;

            Assert.Equal(8 * Mb, budget.LimitBytes);

            using (budget.BeginFrame())
            {
                Assert.Equal(100 * Mb, budget.LimitBytes);
                Assert.Equal(50 * Mb, budget.RetainBytes);
            }

            // Below the floor, the floor applies.
            options.GlyphCacheLimitBytes = 1;

            using (budget.BeginFrame())
            {
                Assert.Equal(4 * Mb, budget.LimitBytes);
            }

            // Unset, the platform's default applies.
            options.GlyphCacheLimitBytes = null;

            using (budget.BeginFrame())
            {
                Assert.Equal(64 * Mb, budget.LimitBytes);
                Assert.Equal(32 * Mb, budget.RetainBytes);
            }
        }

        [Fact]
        public void A_Budget_Without_Options_Takes_The_Platform_Default()
        {
            var budget = new GlyphCacheBudget(() => null, TextRasterizationPlatform.Android);

            using (budget.BeginFrame())
            {
                Assert.Equal(32 * Mb, budget.LimitBytes);
                Assert.Equal(12 * Mb, budget.RetainBytes);
            }
        }

        [Theory]
        [InlineData((int)TextRasterizationPlatform.Windows, 64, 32)]
        [InlineData((int)TextRasterizationPlatform.MacOS, 64, 32)]
        [InlineData((int)TextRasterizationPlatform.Linux, 64, 32)]
        [InlineData((int)TextRasterizationPlatform.Browser, 32, 16)]
        [InlineData((int)TextRasterizationPlatform.Android, 32, 12)]
        [InlineData((int)TextRasterizationPlatform.IOS, 32, 12)]
        [InlineData((int)TextRasterizationPlatform.Other, 16, 8)]
        public void Each_Platform_Has_A_Default_Limit(int platform, long limitMb, long retainMb)
        {
            var (limit, retain) = GlyphCacheBudget.DefaultsFor((TextRasterizationPlatform)platform);

            Assert.Equal(limitMb * Mb, limit);
            Assert.Equal(retainMb * Mb, retain);
        }
    }
}
