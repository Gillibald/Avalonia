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
        /// One row per scenario and fixture, one time column per backend. "Skia / X" above 1 means
        /// X is faster than Skia; "X diff" is the largest channel difference of X's decode to Skia's.
        /// </summary>
        public static string ToMarkdown(BenchmarkReport report)
        {
            var e = report.Environment;
            var text = new StringBuilder();
            var fixtures = report.Fixtures.ToDictionary(f => f.Name);
            var backends = report.Results.Select(r => r.Backend).Distinct().ToList();
            var others = backends.Where(b => b != BenchmarkBackends.Skia.Name).ToList();

            text.AppendLine($"Host: {e.Host}, {e.OperatingSystem}, {e.Architecture}, {e.Runtime}, {e.ProcessorCount} logical processors{(e.Processor is null ? "" : ", " + e.Processor)}.");
            text.AppendLine($"SkiaSharp {e.SkiaSharpVersion}, PixelMan {e.PixelManVersion}{(e.ImageSharpVersion is null ? "" : ", ImageSharp " + e.ImageSharpVersion)}. {e.Warmup} warm-up, {e.Iterations} timed runs, best of {e.Rounds} rounds; {e.Timestamp}.");
            text.AppendLine();

            var columns = new List<string> { "Scenario", "Fixture", "Format" };
            columns.AddRange(backends.Select(b => $"{b} ms"));
            columns.AddRange(others.Select(b => $"Skia / {b}"));
            columns.AddRange(backends.Select(b => $"{b} CPU ms"));
            columns.AddRange(others.Select(b => $"{b} diff"));
            columns.Add("Output");
            columns.Add("Notes");
            text.AppendLine("| " + string.Join(" | ", columns) + " |");
            text.AppendLine("| " + string.Join(" | ", columns.Select((_, i) => i < 3 || i >= columns.Count - 2 ? "---" : "---:")) + " |");

            foreach (var group in report.Results.GroupBy(r => (r.Scenario, r.Fixture)))
            {
                var row = backends.ToDictionary(b => b, b => group.FirstOrDefault(r => r.Backend == b));
                var skia = row.GetValueOrDefault(BenchmarkBackends.Skia.Name);
                var format = fixtures.TryGetValue(group.Key.Fixture, out var fixture) ? fixture.Format : "";
                var cells = new List<string> { group.Key.Scenario, group.Key.Fixture, format };

                cells.AddRange(backends.Select(b => Time(row[b])));
                cells.AddRange(others.Select(b => Ratio(skia, row[b])));
                cells.AddRange(backends.Select(b => Cpu(row[b])));
                cells.AddRange(others.Select(b => report.Comparisons.FirstOrDefault(c =>
                        c.Scenario == group.Key.Scenario && c.Fixture == group.Key.Fixture && c.Backend == b)
                    ?.MaxChannelDifference.ToString(CultureInfo.InvariantCulture) ?? ""));
                cells.Add(Output(row.Values));
                cells.Add(Notes(row.Values));
                text.AppendLine("| " + string.Join(" | ", cells) + " |");
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

        private static string Ratio(ScenarioResult? skia, ScenarioResult? other) =>
            skia is { Supported: true } && other is { Supported: true } && other.MedianMs > 0
                ? (skia.MedianMs / other.MedianMs).ToString("F2", CultureInfo.InvariantCulture)
                : "";

        private static string Output(IEnumerable<ScenarioResult?> results)
        {
            var present = results.Where(r => r?.OutputFormat is not null).ToList();
            var formats = present.Select(r => r!.OutputFormat).Distinct().ToList();

            return formats.Count switch
            {
                0 => "",
                1 => formats[0]!,
                _ => string.Join(", ", present.Select(r => $"{r!.Backend} {r.OutputFormat}")),
            };
        }

        private static string Notes(IEnumerable<ScenarioResult?> results)
        {
            var notes = new List<string>();

            foreach (var result in results)
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
