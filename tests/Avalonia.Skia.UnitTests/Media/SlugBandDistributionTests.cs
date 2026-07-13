using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Measures the Slug band encoder over real fonts: curve counts after quadratic conversion,
    /// chosen band counts, the largest per-band list (which picks the pixel shader's loop
    /// bound), and the per-glyph texel footprint (which must stay under the 2047-texel span the
    /// half-float band offsets can address). The checked-in summary lives in
    /// planning/slug-band-distribution.md; the assertions here are structural gates, not the
    /// measured numbers.
    /// </summary>
    public class SlugBandDistributionTests
    {
        private readonly ITestOutputHelper _output;

        public SlugBandDistributionTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Band_Encoding_Stays_Within_The_Texel_Budget_Across_The_Corpus()
        {
            var report = new List<string>
            {
                "font             glyphs  empty  curves mean/p95/max   bands h/v mean    maxband mean/p95/max   texels mean/p95/max  >2047",
            };
            var failures = new List<string>();

            foreach (var (fileName, label) in new[]
            {
                ("Inter-Regular.ttf", "Inter (glyf)"),
                ("SourceCodePro-Subset.otf", "SourceCP (CFF)"),
            })
            {
                var bytes = LoadFontBytes(fileName);
                using var skData = SKData.CreateCopy(bytes);
                using var skTypeface = SKTypeface.FromData(skData);
                Assert.NotNull(skTypeface);

                var stats = MeasureTypeface(new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None)),
                    label, sampleCap: int.MaxValue, report);

                // Structural gates on the embedded corpus: enough glyphs encode, no band list
                // outruns a plausible shader loop bound, and no payload outruns the offset range.
                if (stats.Encoded < 10)
                {
                    failures.Add($"{label}: only {stats.Encoded} glyphs encoded.");
                }

                if (stats.MaxBandWorst > 64)
                {
                    failures.Add($"{label}: largest band list {stats.MaxBandWorst} exceeds 64 curves.");
                }

                if (stats.OverBudget > 0)
                {
                    failures.Add($"{label}: {stats.OverBudget} glyphs exceed the 2047-texel footprint.");
                }
            }

            // A CJK face stresses band population far beyond any Latin outline. Report-only:
            // system-font availability must not decide a test run.
            if (OperatingSystem.IsWindows())
            {
                using var cjk = SKTypeface.FromFamilyName("Microsoft YaHei");

                if (cjk is not null && cjk.FamilyName.Contains("YaHei", StringComparison.OrdinalIgnoreCase))
                {
                    MeasureTypeface(new GlyphTypeface(new SkiaTypeface(cjk, FontSimulations.None)),
                        "YaHei (CJK)", sampleCap: 2000, report);
                }
                else
                {
                    report.Add("YaHei (CJK)      absent - skipped");
                }
            }

            foreach (var line in report)
            {
                _output.WriteLine(line);
            }

            // Passing-run output is not shown by the terminal reporter; set this variable to a
            // file path to capture the measured table (the source for refreshing the checked-in
            // summary in planning/slug-band-distribution.md).
            if (Environment.GetEnvironmentVariable("SLUG_BAND_REPORT") is { Length: > 0 } reportPath)
            {
                File.WriteAllLines(reportPath, report);
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private readonly record struct TypefaceStats(int Encoded, int MaxBandWorst, int OverBudget);

        private TypefaceStats MeasureTypeface(GlyphTypeface typeface, string label, int sampleCap,
            List<string> report)
        {
            var scale = 1.0 / typeface.Metrics.DesignEmHeight;
            var transform = new Matrix(scale, 0, 0, scale, 0, 0);
            var sink = new SlugContourSink();

            var curveCounts = new List<int>();
            var maxBands = new List<int>();
            var footprints = new List<int>();
            var hBandSum = 0.0;
            var vBandSum = 0.0;
            var empty = 0;
            var overBudget = 0;

            var step = Math.Max(1, typeface.GlyphCount / sampleCap);

            for (var id = 0; id < typeface.GlyphCount; id += step)
            {
                sink.Reset();

                if (!typeface.TryBuildGlyphContours((ushort)id, transform, sink) ||
                    SlugBandEncoder.Encode(sink) is not { } data)
                {
                    empty++;
                    continue;
                }

                var maxBand = 0;
                var entries = 0;

                for (var b = 0; b < data.HorizontalBandCount; b++)
                {
                    maxBand = Math.Max(maxBand, data.GetHorizontalBand(b).Length);
                    entries += data.GetHorizontalBand(b).Length;
                }

                for (var b = 0; b < data.VerticalBandCount; b++)
                {
                    maxBand = Math.Max(maxBand, data.GetVerticalBand(b).Length);
                    entries += data.GetVerticalBand(b).Length;
                }

                // Curve texels: one per curve plus a terminator per contour; band texels: one
                // header per band plus one per list entry. Row-break duplicates are ignored here
                // (bounded by curve-texel count / 2048 and irrelevant at these magnitudes).
                var curveTexels = data.TotalCurveCount + data.ContourCount;
                var bandTexels = data.HorizontalBandCount + data.VerticalBandCount + entries;

                curveCounts.Add(data.TotalCurveCount);
                maxBands.Add(maxBand);
                footprints.Add(curveTexels + bandTexels);
                hBandSum += data.HorizontalBandCount;
                vBandSum += data.VerticalBandCount;

                if (bandTexels > 2047)
                {
                    overBudget++;
                }
            }

            if (curveCounts.Count == 0)
            {
                report.Add($"{label,-16} no glyphs encoded");
                return new TypefaceStats(0, 0, 0);
            }

            report.Add(FormattableString.Invariant(
                $"{label,-16} {curveCounts.Count,6} {empty,6}  {curveCounts.Average(),6:0.0}/{Percentile(curveCounts, 0.95),3}/{curveCounts.Max(),4}   {hBandSum / curveCounts.Count,4:0.0}/{vBandSum / curveCounts.Count,4:0.0}      {maxBands.Average(),5:0.0}/{Percentile(maxBands, 0.95),3}/{maxBands.Max(),4}   {footprints.Average(),7:0.0}/{Percentile(footprints, 0.95),4}/{footprints.Max(),5}  {overBudget,4}"));

            return new TypefaceStats(curveCounts.Count, maxBands.Max(), overBudget);
        }

        private static int Percentile(List<int> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToList();

            return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            // Walk up from the test output directory to the repo's tests folder — the same
            // convention the render tests use — so the .otf corpus needs no embedding.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
