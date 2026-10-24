using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>JSON for monitoring, Markdown for reading, and the comparison of two runs.</summary>
    public static class ReportWriter
    {
        public static string ToJson(BenchmarkReport report) =>
            JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.BenchmarkReport);

        public static BenchmarkReport FromJson(string json) =>
            JsonSerializer.Deserialize(json, BenchmarkJsonContext.Default.BenchmarkReport)
            ?? throw new FormatException("Not a benchmark report.");

        /// <summary>
        /// One row per scenario and fixture. "Skia / PixelMan" above 1 means PixelMan is faster.
        /// </summary>
        public static string ToMarkdown(BenchmarkReport report)
        {
            var e = report.Environment;
            var text = new StringBuilder();
            var fixtures = report.Fixtures.ToDictionary(f => f.Name);

            text.AppendLine($"Host: {e.Host}, {e.OperatingSystem}, {e.Architecture}, {e.Runtime}, {e.ProcessorCount} logical processors{(e.Processor is null ? "" : ", " + e.Processor)}.");
            text.AppendLine($"SkiaSharp {e.SkiaSharpVersion}, PixelMan {e.PixelManVersion}. {e.Warmup} warm-up, {e.Iterations} timed runs, best of {e.Rounds} rounds; {e.Timestamp}.");
            text.AppendLine();
            text.AppendLine("| Scenario | Fixture | Format | Skia ms | PixelMan ms | Skia / PixelMan | Skia CPU ms | PixelMan CPU ms | Max diff | Output | Notes |");
            text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");

            foreach (var group in report.Results.GroupBy(r => (r.Scenario, r.Fixture)))
            {
                var skia = group.FirstOrDefault(r => r.Backend == BenchmarkBackends.Skia.Name);
                var pixelMan = group.FirstOrDefault(r => r.Backend == BenchmarkBackends.PixelMan.Name);
                var comparison = report.Comparisons.FirstOrDefault(c =>
                    c.Scenario == group.Key.Scenario && c.Fixture == group.Key.Fixture);
                var format = fixtures.TryGetValue(group.Key.Fixture, out var fixture) ? fixture.Format : "";
                var ratio = skia is { Supported: true } && pixelMan is { Supported: true } && pixelMan.MedianMs > 0
                    ? (skia.MedianMs / pixelMan.MedianMs).ToString("F2", CultureInfo.InvariantCulture)
                    : "";

                text.AppendLine($"| {group.Key.Scenario} | {group.Key.Fixture} | {format} | {Time(skia)} | {Time(pixelMan)} | {ratio} | {Cpu(skia)} | {Cpu(pixelMan)} | {comparison?.MaxChannelDifference.ToString(CultureInfo.InvariantCulture) ?? ""} | {Output(skia, pixelMan)} | {Notes(skia, pixelMan)} |");
            }

            return text.ToString();
        }

        /// <summary>Median changes of <paramref name="current"/> against <paramref name="baseline"/> beyond the threshold.</summary>
        public static string Compare(BenchmarkReport baseline, BenchmarkReport current, double thresholdPercent)
        {
            var text = new StringBuilder();
            var before = baseline.Results.Where(r => r.Supported).ToDictionary(r => (r.Scenario, r.Fixture, r.Backend));
            var changed = 0;

            var mismatched = baseline.Fixtures.Join(current.Fixtures, f => f.Name, f => f.Name, (a, b) => (a, b))
                .Where(p => p.a.Sha256 != p.b.Sha256).Select(p => p.a.Name).ToList();

            if (mismatched.Count > 0)
                text.AppendLine($"Fixtures differ between the runs, so their timings do not compare: {string.Join(", ", mismatched)}.");

            text.AppendLine("| Scenario | Fixture | Backend | Before ms | After ms | Change |");
            text.AppendLine("| --- | --- | --- | ---: | ---: | ---: |");

            foreach (var result in current.Results.Where(r => r.Supported))
            {
                if (!before.TryGetValue((result.Scenario, result.Fixture, result.Backend), out var old) || old.MedianMs <= 0)
                    continue;

                var change = (result.MedianMs - old.MedianMs) / old.MedianMs * 100;

                if (Math.Abs(change) < thresholdPercent)
                    continue;

                changed++;
                text.AppendLine(FormattableString.Invariant(
                    $"| {result.Scenario} | {result.Fixture} | {result.Backend} | {old.MedianMs:F2} | {result.MedianMs:F2} | {change:+0.0;-0.0}% |"));
            }

            text.AppendLine();
            text.AppendLine(changed == 0
                ? FormattableString.Invariant($"No median moved by {thresholdPercent:F0}% or more.")
                : FormattableString.Invariant($"{changed} medians moved by {thresholdPercent:F0}% or more."));

            return text.ToString();
        }

        private static string Time(ScenarioResult? result) =>
            result is { Supported: true }
                ? result.MedianMs.ToString("F2", CultureInfo.InvariantCulture)
                : "n/a";

        private static string Cpu(ScenarioResult? result) =>
            result?.CpuMedianMs is { } cpu ? cpu.ToString("F2", CultureInfo.InvariantCulture) : "";

        private static string Output(ScenarioResult? skia, ScenarioResult? pixelMan)
        {
            var formats = new[] { skia, pixelMan }.Select(r => r?.OutputFormat).Where(f => f is not null).Distinct().ToList();

            return formats.Count switch
            {
                0 => "",
                1 => formats[0]!,
                _ => $"Skia {skia?.OutputFormat}, PixelMan {pixelMan?.OutputFormat}",
            };
        }

        private static string Notes(ScenarioResult? skia, ScenarioResult? pixelMan)
        {
            var notes = new List<string>();

            foreach (var result in new[] { skia, pixelMan })
            {
                if (result is null)
                    continue;

                if (!result.Supported)
                    notes.Add($"{result.Backend}: unsupported");
                else if (result.OutputBytes is { } bytes)
                {
                    var quality = result.PsnrDb is { } psnr
                        ? FormattableString.Invariant($", {psnr:F1} dB")
                        : "";

                    notes.Add(FormattableString.Invariant($"{result.Backend}: {bytes / 1024.0:F0} KiB") + quality);
                }
            }

            return string.Join("; ", notes);
        }
    }
}
