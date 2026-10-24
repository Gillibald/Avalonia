using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Imaging.Benchmarks
{
    public sealed record BenchmarkOptions
    {
        public int Warmup { get; init; } = 3;
        public int Iterations { get; init; } = 15;
        public int Rounds { get; init; } = 2;

        /// <summary>Runs only scenarios or fixtures whose id contains this text.</summary>
        public string? Filter { get; init; }

        /// <summary>A directory of third-party images added to every decode scenario.</summary>
        public string? ImageDirectory { get; init; }

        public static BenchmarkOptions Quick { get; } = new() { Warmup = 1, Iterations = 5, Rounds = 1 };
    }

    /// <summary>
    /// Runs every scenario on both backends: decodes to Skia's render format (Bgra8888,
    /// premultiplied) and encodes with Avalonia's encoder settings, alternating the backends
    /// and keeping each one's best round, then compares their outputs.
    /// </summary>
    public sealed class BenchmarkRunner
    {
        private readonly BenchmarkOptions _options;
        private readonly Action<string> _log;

        public BenchmarkRunner(BenchmarkOptions options, Action<string> log)
        {
            _options = options;
            _log = log;
        }

        public BenchmarkReport Run(string host)
        {
            _log("building fixtures");

            return Run(host, Fixtures.Create(_options.ImageDirectory, _log), Fixtures.EncodeSourcePixels());
        }

        /// <summary>Runs on fixtures built elsewhere, with the encode scenarios' source pixels.</summary>
        public BenchmarkReport Run(string host, IReadOnlyList<Fixture> fixtures, byte[] encodeSource)
        {
            var source = Fixtures.EncodeSource(encodeSource);
            var scenarios = Scenarios(fixtures, source).Where(Matches).ToList();
            var results = new List<ScenarioResult>();
            var comparisons = new List<OutputComparison>();

            foreach (var scenario in scenarios)
            {
                var runs = BenchmarkBackends.All.ToDictionary(b => b.Name, _ => new BackendRuns());

                // Rounds alternate the backends, so drift in the machine hits both alike.
                for (var round = 0; round < _options.Rounds; round++)
                {
                    foreach (var backend in BenchmarkBackends.All)
                        MeasureRound(scenario, backend, runs[backend.Name]);
                }

                foreach (var backend in BenchmarkBackends.All)
                {
                    results.Add(runs[backend.Name].ToResult(scenario, backend.Name));
                    _log($"{scenario.Id} {scenario.Fixture} {backend.Name}: " +
                         (results[^1].Supported ? $"{results[^1].MedianMs:F2} ms" : "unsupported"));
                }

                if (scenario.Kind == ScenarioKind.Decode &&
                    runs[BenchmarkBackends.Skia.Name].Outcome is { Pixels: { } skia } skiaOutcome &&
                    runs[BenchmarkBackends.PixelMan.Name].Outcome is { Pixels: { } pixelMan } pixelManOutcome &&
                    skia.Length == pixelMan.Length &&
                    skiaOutcome.Format?.Split(' ')[0] == pixelManOutcome.Format?.Split(' ')[0])
                {
                    comparisons.Add(Compare(scenario, skia, pixelMan));
                }
            }

            return new BenchmarkReport
            {
                Environment = Environment(host),
                Fixtures = fixtures.Select(Fixtures.Describe).ToList(),
                Results = results,
                Comparisons = comparisons,
            };
        }

        private bool Matches(Scenario scenario) =>
            _options.Filter is not { } filter ||
            scenario.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            scenario.Fixture.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<Scenario> Scenarios(IReadOnlyList<Fixture> fixtures, PixelBuffer source)
        {
            foreach (var fixture in fixtures)
                yield return Scenario.Decode("decode", fixture, null);

            foreach (var fixture in fixtures.Where(f => f.Name is "photo-jpeg" or "photo-png"))
                yield return Scenario.Decode("decode-quarter", fixture, new PixelSize(fixture.Width / 4, 0));

            foreach (var fixture in fixtures)
                yield return Scenario.CodecOnly(fixture);

            yield return Scenario.Encode("encode-png", source, () => new PngBitmapEncoder { CompressionLevel = 6 }, false);
            yield return Scenario.Encode("encode-jpeg", source, () => new JpegBitmapEncoder { Quality = 90 }, true);
            yield return Scenario.Encode("encode-webp", source, () => new WebpBitmapEncoder { Quality = 75 }, true);
        }

        private void MeasureRound(Scenario scenario, BenchmarkBackend backend, BackendRuns runs)
        {
            if (runs.Error is not null)
                return;

            using var scope = BenchmarkBackends.Bind(backend.Create());

            try
            {
                // The first run checks support, captures the output and counts as a warm-up.
                if (runs.Outcome is null)
                    runs.Outcome = scenario.RunAndCapture(backend.Name);
                else
                    scenario.Run(backend.Name);
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                runs.Error = e.GetType().Name + ": " + e.Message;
                return;
            }

            for (var i = 1; i < _options.Warmup; i++)
                scenario.Run(backend.Name);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var wall = new List<double>(_options.Iterations);
            var cpuBefore = ProcessCpu();

            for (var i = 0; i < _options.Iterations; i++)
            {
                var stopwatch = Stopwatch.StartNew();

                scenario.Run(backend.Name);

                wall.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            double? cpu = cpuBefore is { } before && ProcessCpu() is { } after
                ? (after - before).TotalMilliseconds / _options.Iterations
                : null;

            wall.Sort();

            // Each backend keeps its best round.
            if (runs.Wall is null || Median(wall) < Median(runs.Wall))
            {
                runs.Wall = wall;
                runs.Cpu = cpu;
            }
        }

        /// <summary>One backend's rounds of one scenario.</summary>
        private sealed class BackendRuns
        {
            public ScenarioOutcome? Outcome { get; set; }
            public List<double>? Wall { get; set; }
            public double? Cpu { get; set; }
            public string? Error { get; set; }

            public ScenarioResult ToResult(Scenario scenario, string backend)
            {
                if (Error is not null || Wall is null || Outcome is null)
                {
                    return new ScenarioResult
                    {
                        Scenario = scenario.Id, Fixture = scenario.Fixture, Backend = backend, Supported = false,
                        Error = Error,
                    };
                }

                return new ScenarioResult
                {
                    Scenario = scenario.Id,
                    Fixture = scenario.Fixture,
                    Backend = backend,
                    Supported = true,
                    MedianMs = Round(Median(Wall)),
                    MinMs = Round(Wall[0]),
                    P90Ms = Round(Wall[(int)Math.Ceiling(Wall.Count * 0.9) - 1]),
                    CpuMedianMs = Cpu is { } cpu ? Round(cpu) : null,
                    OutputWidth = Outcome.Width,
                    OutputHeight = Outcome.Height,
                    OutputFormat = Outcome.Format,
                    OutputBytes = Outcome.EncodedBytes,
                    PsnrDb = Outcome.PsnrDb is { } psnr ? Math.Round(psnr, 2) : null,
                };
            }
        }

        private static OutputComparison Compare(Scenario scenario, byte[] a, byte[] b)
        {
            var max = 0;
            long sum = 0;

            for (var i = 0; i < a.Length; i++)
            {
                var d = Math.Abs(a[i] - b[i]);

                max = Math.Max(max, d);
                sum += d;
            }

            return new OutputComparison
            {
                Scenario = scenario.Id, Fixture = scenario.Fixture, MaxChannelDifference = max,
                MeanAbsoluteDifference = Math.Round((double)sum / a.Length, 4),
            };
        }

        private static TimeSpan? ProcessCpu()
        {
            if (OperatingSystem.IsBrowser())
                return null;

            try
            {
                return Process.GetCurrentProcess().TotalProcessorTime;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static double Median(List<double> sorted) => sorted[sorted.Count / 2];

        private static double Round(double value) => Math.Round(value, 3);

        private EnvironmentInfo Environment(string host) => new()
        {
            Host = host,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = System.Environment.ProcessorCount,
            Processor = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            SkiaSharpVersion = Version(typeof(SkiaSharp.SKImage).Assembly),
            PixelManVersion = Version(typeof(Avalonia.PixelMan.PmBitmapFrame).Assembly),
            Timestamp = DateTimeOffset.UtcNow.ToString("u"),
            Warmup = _options.Warmup,
            Iterations = _options.Iterations,
            Rounds = _options.Rounds,
        };

        private static string Version(Assembly assembly) =>
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
