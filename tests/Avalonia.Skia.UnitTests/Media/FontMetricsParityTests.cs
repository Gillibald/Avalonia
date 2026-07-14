using System;
using Avalonia.Media;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The managed <see cref="GlyphTypeface"/> must report the same font-wide metrics as the
    /// platform stack (SkiaSharp — DirectWrite on Windows), or every text line measures a
    /// different height than upstream Avalonia. The load-bearing case is fonts WITHOUT the
    /// OS/2 USE_TYPO_METRICS flag: the platform sizes those by usWinAscent/usWinDescent (the
    /// font's clipping region — Segoe UI Emoji's ink tops exceed its hhea ascender by 18%),
    /// with hhea's overshoot mapped to external leading.
    /// </summary>
    public class FontMetricsParityTests
    {
        [Theory]
        [InlineData("Segoe UI Emoji")]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        [InlineData("Bahnschrift")]
        public void Managed_Font_Metrics_Match_The_Platform(string family)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Compares against the Windows-shipped fonts.");

            using var skTypeface = SKTypeface.FromFamilyName(family);

            Assert.SkipWhen(skTypeface is null || !skTypeface.FamilyName.StartsWith(family.Split(' ')[0]),
                $"{family} is not installed.");

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new FontManagerImpl()));

            var managed = new Typeface(family).GlyphTypeface;
            var metrics = managed.Metrics;

            using var skFont = new SKFont(skTypeface, skTypeface!.UnitsPerEm);
            var platform = skFont.Metrics;

            // Both stacks store ascent negative-up. A unit of slack absorbs float rounding.
            Assert.True(Math.Abs(metrics.Ascent - platform.Ascent) <= 2,
                $"{family}: managed ascent {metrics.Ascent} vs platform {platform.Ascent}");
            Assert.True(Math.Abs(metrics.Descent - platform.Descent) <= 2,
                $"{family}: managed descent {metrics.Descent} vs platform {platform.Descent}");
            Assert.True(Math.Abs(metrics.LineGap - platform.Leading) <= 2,
                $"{family}: managed line gap {metrics.LineGap} vs platform leading {platform.Leading}");
        }
    }
}
