using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Measures where bytecode hinting costs land: per-size program setup (fpgm plus prep,
    /// paid once per size state) and cold mask builds (the glyph program runs per build),
    /// against the auto-hinter on the same outlines. Warm frames draw cached masks and never
    /// execute either engine, so churn - many unique glyphs building cold, the CJK scroll
    /// case - is the only scenario hinting cost can reach.
    ///
    /// Set TRUETYPE_COST_PROBE=1 to run; the report lands in the temp directory.
    /// </summary>
    public class TrueTypeHintingCostProbe
    {
        [Fact]
        public void Measure_Setup_And_Cold_Build_Costs()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("TRUETYPE_COST_PROBE") is not "1",
                "measurement probe; set TRUETYPE_COST_PROBE=1 to run");

            var report = new System.Text.StringBuilder();

            report.AppendLine("font: size-state setup | cold hinted build /glyph | cold autohint build /glyph");

            MeasureSyntheticFont(report, "NotoMono (committed fixture)",
                TestFontLoader.LoadNotoMono());

            if (File.Exists(@"C:\Windows\Fonts\tahoma.ttf"))
            {
                MeasureSyntheticFont(report, "Tahoma",
                    File.ReadAllBytes(@"C:\Windows\Fonts\tahoma.ttf"));
            }

            // CJK churn: TTC containers skip the stripped comparison (table surgery reads
            // plain sfnt), so YaHei reports the bytecode cold cost alone - the number that
            // has to disappear into scroll-frame budgets.
            if (File.Exists(@"C:\Windows\Fonts\msyh.ttc"))
            {
                using var skTypeface = SKTypeface.FromFile(@"C:\Windows\Fonts\msyh.ttc", 0);

                if (skTypeface is not null)
                {
                    var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));
                    var (setup, hinted) = MeasureTypeface(typeface, glyphBase: 5000);

                    report.AppendLine(FormattableString.Invariant(
                        $"Microsoft YaHei (CJK): {setup:0.00} ms | {hinted:0.0} us | (ttc - no stripped variant)"));
                }
            }

            var path = Path.Combine(Path.GetTempPath(), "truetype-cost.txt");

            File.WriteAllText(path, report.ToString());
            Assert.Fail("report written to " + path + Environment.NewLine + report);
        }

        private static void MeasureSyntheticFont(System.Text.StringBuilder report, string label, byte[] bytes)
        {
            var instructed = SyntheticFont.FromBytes(bytes).CreateGlyphTypeface();
            var stripped = SyntheticFont.FromBytes(bytes);

            stripped.Remove("fpgm");
            stripped.Remove("prep");
            stripped.Remove("cvt ");

            var autoHinted = stripped.CreateGlyphTypeface();

            var (setup, hinted) = MeasureTypeface(instructed, glyphBase: 1);
            var (_, auto) = MeasureTypeface(autoHinted, glyphBase: 1);

            report.AppendLine(FormattableString.Invariant(
                $"{label}: {setup:0.00} ms | {hinted:0.0} us | {auto:0.0} us"));
        }

        /// <summary>Setup = a fresh hinter for an unseen scale (fpgm+prep, the once-per-size
        /// cost). Cold builds = GlyphMasks.Build straight through, no cache, over a spread of
        /// distinct glyphs at 12 px.</summary>
        private static (double SetupMs, double ColdBuildUs) MeasureTypeface(GlyphTypeface typeface, int glyphBase)
        {
            const int buildCount = 200;

            // Distinct sizes so every setup probe creates a fresh size state.
            var setupWatch = new Stopwatch();
            const int setupRounds = 8;

            for (var i = 0; i < setupRounds; i++)
            {
                var scaleQ = GlyphMaskKey.QuantizeScale(20f + i * 0.5f);

                setupWatch.Start();
                typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased);
                setupWatch.Stop();
            }

            using var scratch = new GlyphPathBuilder();
            var scaleQ12 = GlyphMaskKey.QuantizeScale(12f);

            // Prime the 12 px size state so builds measure the per-glyph cost alone.
            typeface.GetTrueTypeHinter(scaleQ12, GlyphMaskMode.Antialiased);

            var glyphs = new ushort[buildCount];
            var step = Math.Max(1, (typeface.GlyphCount - glyphBase) / buildCount);

            for (var i = 0; i < buildCount; i++)
            {
                glyphs[i] = (ushort)Math.Min(typeface.GlyphCount - 1, glyphBase + i * step);
            }

            // One warm pass off the clock (JIT, table parses), then the measured cold loop -
            // Build itself never caches, so every call is a full cold rasterization.
            foreach (var glyph in glyphs)
            {
                GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(glyph, scaleQ12, 0,
                    GlyphMaskMode.Antialiased, GridFit: true));
            }

            var buildWatch = Stopwatch.StartNew();

            foreach (var glyph in glyphs)
            {
                GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(glyph, scaleQ12, 0,
                    GlyphMaskMode.Antialiased, GridFit: true));
            }

            buildWatch.Stop();

            return (setupWatch.Elapsed.TotalMilliseconds / setupRounds,
                buildWatch.Elapsed.TotalMilliseconds * 1000 / buildCount);
        }
    }

    file static class TestFontLoader
    {
        public static byte[] LoadNotoMono()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent!;
            }

            return File.ReadAllBytes(Path.Combine(directory!.FullName,
                "Avalonia.RenderTests", "Assets", "NotoMono-Regular.ttf"));
        }
    }
}
