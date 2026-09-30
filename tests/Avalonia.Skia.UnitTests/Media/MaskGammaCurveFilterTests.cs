using System;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The closed-form gamma filter behind Slug text against <see cref="MaskGamma"/>'s tables,
    /// which are the specification: every luminance bucket, every 8-bit coverage, through the
    /// same composed tint filter the Slug draw uses.
    /// </summary>
    public class MaskGammaCurveFilterTests
    {
        [Fact]
        public void The_Closed_Form_Effect_Compiles()
        {
            Assert.True(MaskGammaCurveFilters.IsSupported);
            Assert.NotSame(MaskGammaFilters.Get(0, 0, 0), MaskGammaCurveFilters.Get(0, 0, 0));
        }

        [Fact]
        public void Filters_Are_Shared_Per_Luminance_Bucket()
        {
            // 0x10 and 0x1F share bucket 0; 0x20 starts bucket 1.
            Assert.Same(MaskGammaCurveFilters.Get(0x10, 0x10, 0x10), MaskGammaCurveFilters.Get(0x1F, 0x1F, 0x1F));
            Assert.NotSame(MaskGammaCurveFilters.Get(0x10, 0x10, 0x10), MaskGammaCurveFilters.Get(0x20, 0x20, 0x20));
        }

        [Fact]
        public void The_Closed_Form_Filter_Matches_The_Tables_On_The_Raster_Pipeline()
        {
            using var surface = SKSurface.Create(RampInfo);

            AssertMatchesTables(surface, null, "raster");
        }

        internal static SKImageInfo RampInfo { get; } =
            new(256, MaskGamma.BucketCount, SKColorType.Rgba8888, SKAlphaType.Premul);

        /// <summary>
        /// Draws one coverage ramp per luminance bucket through the Slug tint filter and checks
        /// every output alpha against the bucket's table: within one 8-bit level, and exact at
        /// the endpoints, which the tables pin so zero coverage never leaks ink and full
        /// coverage stays opaque.
        /// </summary>
        internal static void AssertMatchesTables(SKSurface surface, GRContext? grContext, string backend)
        {
            // The Slug shaders emit coverage as premultiplied white, so every channel is alpha.
            var ramp = new byte[256 * 4 * MaskGamma.BucketCount];

            for (var i = 0; i < ramp.Length; i++)
            {
                ramp[i] = (byte)(i / 4 % 256);
            }

            using var image = SKImage.FromPixelCopy(RampInfo, ramp, 256 * 4);

            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);

            for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
            {
                // A gray whose luma lands mid-bucket, so the row exercises exactly that bucket.
                var gray = (uint)(bucket * 32 + 16);

                using var artifact = new SlugRunArtifact();
                using var paint = new SKPaint();

                paint.BlendMode = SKBlendMode.Src;
                paint.ColorFilter = artifact.GetFilter(0xFF000000 | gray << 16 | gray << 8 | gray);

                var row = new SKRect(0, bucket, 256, bucket + 1);

                canvas.DrawImage(image, row, row, new SKSamplingOptions(), paint);
            }

            canvas.Flush();
            grContext?.Flush();

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(RampInfo);

            Assert.True(snapshot.ReadPixels(RampInfo, readback.GetPixels(), readback.RowBytes, 0, 0));

            var maxError = 0;
            var worst = "";

            for (var bucket = 0; bucket < MaskGamma.BucketCount; bucket++)
            {
                var table = MaskGamma.GetTable(bucket);

                for (var coverage = 0; coverage < 256; coverage++)
                {
                    var actual = readback.GetPixel(coverage, bucket).Alpha;
                    var error = Math.Abs(actual - table[coverage]);

                    if (error > maxError)
                    {
                        maxError = error;
                        worst = $" Worst: bucket {bucket}, coverage {coverage}: table {table[coverage]}, filter {actual}.";
                    }
                }

                Assert.True(readback.GetPixel(0, bucket).Alpha == 0,
                    $"{backend}: bucket {bucket} leaks {readback.GetPixel(0, bucket).Alpha} at zero coverage.");
                Assert.True(readback.GetPixel(255, bucket).Alpha == 255,
                    $"{backend}: bucket {bucket} reaches only {readback.GetPixel(255, bucket).Alpha} at full coverage.");
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{backend}: max error {maxError}.{worst}");

            Assert.True(maxError <= 1, $"{backend}: max error {maxError} levels.{worst}");
        }
    }
}
