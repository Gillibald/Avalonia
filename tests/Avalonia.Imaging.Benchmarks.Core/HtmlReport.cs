using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>
    /// One HTML page from reports of any number of machines and hosts. Reports with the same leg
    /// name on the same machine are merged, keeping each case's best median, so repeated runs and
    /// runs collected from other people's machines go in side by side. The leg name comes from
    /// the caller, because the environment alone cannot tell apart, say, the browser's
    /// interpreter and AOT builds.
    /// </summary>
    public static class HtmlReport
    {
        private static readonly string[] s_summaryScenarios =
            ["decode", "decode-codec", "decode-quarter", "encode-png", "encode-jpeg", "encode-webp"];

        private sealed record Leg(string Title, string Machine, List<BenchmarkReport> Runs)
        {
            public Dictionary<(string Scenario, string Fixture, string Backend), double> Best { get; } = new();
            public List<(string Scenario, string Fixture)> Order { get; } = new();
        }

        /// <summary>
        /// The leg name for a report file: its name without the extension and a trailing run
        /// number, so <c>windows-1.json</c> and <c>windows-2.json</c> form the leg <c>windows</c>.
        /// </summary>
        public static string LegName(string path)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var dash = name.LastIndexOf('-');
            return dash > 0 && name[(dash + 1)..].All(char.IsAsciiDigit) ? name[..dash] : name;
        }

        public static string Render(IReadOnlyList<(string Leg, BenchmarkReport Report)> inputs)
        {
            var reports = inputs.Select(i => i.Report).ToList();
            var legs = inputs
                .GroupBy(i => (i.Leg, i.Report.Environment.Processor, i.Report.Environment.OperatingSystem))
                .Select(g => Merge(g.Key.Leg, g.Select(i => i.Report).ToList()))
                .OrderBy(l => l.Title, StringComparer.Ordinal)
                .ToList();
            var backends = reports.SelectMany(r => r.Results).Select(r => r.Backend).Distinct().ToList();
            var others = backends.Where(b => b != BenchmarkBackends.Skia.Name).ToList();
            var html = new StringBuilder();

            html.Append("""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Imaging benchmarks</title>
                <style>
                :root { --bg: #fff; --fg: #1f2328; --muted: #59636e; --line: #d1d9e0; --faster: #1a7f37; --slower: #cf222e; }
                @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1; --line: #3d444d; --faster: #3fb950; --slower: #f85149; } }
                body { background: var(--bg); color: var(--fg); font: 14px/1.45 system-ui, sans-serif; margin: 0 auto; max-width: 1100px; padding: 16px; }
                h1 { font-size: 22px; } h2 { font-size: 17px; margin-top: 28px; }
                p, .note { color: var(--muted); }
                .frame { overflow-x: auto; }
                table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
                th, td { border-bottom: 1px solid var(--line); padding: 4px 8px; text-align: left; vertical-align: top; }
                td.num, th.num { text-align: right; white-space: nowrap; }
                .faster { color: var(--faster); } .slower { color: var(--slower); }
                details { margin-top: 12px; } summary { cursor: pointer; font-weight: 600; }
                </style></head><body>
                <h1>Imaging benchmarks</h1>
                <p>Time of each backend relative to SkiaSharp on the same inputs: below 1.00 is faster. Summary cells are geometric means over a scenario's fixtures; each leg keeps the best median of its runs.</p>
                """);

            html.Append("<h2>Summary</h2><div class=\"frame\"><table><thead><tr><th>Leg</th><th>Backend</th>");
            var scenarios = s_summaryScenarios.Where(s => legs.Any(l => l.Order.Any(o => o.Scenario == s))).ToList();
            foreach (var scenario in scenarios)
                html.Append($"<th class=\"num\">{Encode(scenario)}</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var leg in legs)
            {
                foreach (var backend in others)
                {
                    html.Append($"<tr><th scope=\"row\">{Encode(leg.Title)}<div class=\"note\">{Encode(leg.Machine)}</div></th><td>{Encode(backend)}</td>");
                    foreach (var scenario in scenarios)
                        html.Append($"<td class=\"num\">{Ratio(GeometricMean(Ratios(leg, scenario, backend)))}</td>");
                    html.Append("</tr>");
                }
            }
            html.Append("</tbody></table></div>");

            html.Append("<h2>Per leg</h2>");
            foreach (var leg in legs)
            {
                var environment = leg.Runs[0].Environment;
                html.Append($"<details><summary>{Encode(leg.Title)}</summary>");
                html.Append($"<p>{Encode(leg.Machine)}. SkiaSharp {Encode(Short(environment.SkiaSharpVersion))}, PixelMan {Encode(Short(environment.PixelManVersion))}");
                if (environment.ImageSharpVersion is { } imageSharp)
                    html.Append($", ImageSharp {Encode(Short(imageSharp))}");
                html.Append($". {leg.Runs.Count} run{(leg.Runs.Count == 1 ? "" : "s")}, latest {Encode(leg.Runs.Max(r => r.Environment.Timestamp)!)}.</p>");
                html.Append("<div class=\"frame\"><table><thead><tr><th>Scenario</th><th>Fixture</th>");
                foreach (var backend in backends)
                    html.Append($"<th class=\"num\">{Encode(backend)} ms</th>");
                foreach (var backend in others)
                    html.Append($"<th class=\"num\">{Encode(backend)} / Skia</th>");
                html.Append("</tr></thead><tbody>");
                foreach (var (scenario, fixture) in leg.Order)
                {
                    html.Append($"<tr><td>{Encode(scenario)}</td><td>{Encode(fixture)}</td>");
                    foreach (var backend in backends)
                        html.Append($"<td class=\"num\">{Milliseconds(Lookup(leg, scenario, fixture, backend))}</td>");
                    var skia = Lookup(leg, scenario, fixture, BenchmarkBackends.Skia.Name);
                    foreach (var backend in others)
                        html.Append($"<td class=\"num\">{Ratio(Lookup(leg, scenario, fixture, backend) / skia)}</td>");
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></div></details>");
            }

            html.Append("</body></html>\n");
            return html.ToString();
        }

        private static Leg Merge(string name, List<BenchmarkReport> runs)
        {
            var environment = runs[0].Environment;
            var machine = string.Join(", ", new[]
            {
                environment.Host, environment.Architecture, environment.Processor,
                $"{environment.ProcessorCount} logical processors", environment.OperatingSystem,
                environment.Runtime, environment.Scheduling,
            }.Where(p => !string.IsNullOrEmpty(p)));
            var leg = new Leg(name, machine, runs);

            foreach (var result in runs.SelectMany(r => r.Results))
            {
                var caseKey = (result.Scenario, result.Fixture);
                if (!leg.Order.Contains(caseKey))
                    leg.Order.Add(caseKey);
                if (!result.Supported || result.MedianMs <= 0)
                    continue;
                var key = (result.Scenario, result.Fixture, result.Backend);
                if (!leg.Best.TryGetValue(key, out var best) || result.MedianMs < best)
                    leg.Best[key] = result.MedianMs;
            }

            return leg;
        }

        private static double? Lookup(Leg leg, string scenario, string fixture, string backend) =>
            leg.Best.TryGetValue((scenario, fixture, backend), out var value) ? value : null;

        private static IEnumerable<double> Ratios(Leg leg, string scenario, string backend) =>
            leg.Order.Where(o => o.Scenario == scenario)
                .Select(o => Lookup(leg, scenario, o.Fixture, backend) / Lookup(leg, scenario, o.Fixture, BenchmarkBackends.Skia.Name))
                .Where(r => r is > 0)
                .Select(r => r!.Value);

        private static double? GeometricMean(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? null : Math.Exp(list.Average(Math.Log));
        }

        private static string Ratio(double? ratio) => ratio is not { } r
            ? ""
            : $"<span class=\"{(r < 0.95 ? "faster" : r > 1.05 ? "slower" : "")}\">{r.ToString("0.00", CultureInfo.InvariantCulture)}</span>";

        private static string Milliseconds(double? value) => value is not { } v
            ? "<span class=\"note\">n/a</span>"
            : v.ToString(v < 10 ? "0.000" : "0.0", CultureInfo.InvariantCulture);

        private static string Short(string version) => version.Split('+')[0];

        private static string Encode(string text) => WebUtility.HtmlEncode(text);
    }
}
