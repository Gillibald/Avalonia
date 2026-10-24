using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>One run of the suite on one platform: what ran, on what, with which inputs.</summary>
    public sealed class BenchmarkReport
    {
        public required EnvironmentInfo Environment { get; init; }
        public required List<FixtureInfo> Fixtures { get; init; }
        public required List<ScenarioResult> Results { get; init; }
        public required List<OutputComparison> Comparisons { get; init; }
    }

    public sealed class EnvironmentInfo
    {
        public required string Host { get; init; }
        public required string OperatingSystem { get; init; }
        public required string Architecture { get; init; }
        public required string Runtime { get; init; }
        public required int ProcessorCount { get; init; }
        public string? Processor { get; init; }
        public required string SkiaSharpVersion { get; init; }
        public required string PixelManVersion { get; init; }
        public required string Timestamp { get; init; }
        public required int Warmup { get; init; }
        public required int Iterations { get; init; }
        public required int Rounds { get; init; }
    }

    public sealed class FixtureInfo
    {
        public required string Name { get; init; }
        public required string Format { get; init; }
        public required string Content { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Bytes { get; init; }
        public required string Encoder { get; init; }
        public required string Sha256 { get; init; }
    }

    /// <summary>The timings of one scenario on one fixture with one backend.</summary>
    public sealed class ScenarioResult
    {
        public required string Scenario { get; init; }
        public required string Fixture { get; init; }
        public required string Backend { get; init; }
        public required bool Supported { get; init; }
        public string? Error { get; init; }
        public double MedianMs { get; init; }
        public double MinMs { get; init; }
        public double P90Ms { get; init; }

        /// <summary>
        /// Process CPU time per run, averaged over the timed runs (the OS clock is too coarse for
        /// single runs); null where the platform does not report it (the browser). Above the wall
        /// time, the backend used more than one thread.
        /// </summary>
        public double? CpuMedianMs { get; init; }

        public int OutputWidth { get; init; }
        public int OutputHeight { get; init; }

        /// <summary>The decoded pixel and alpha format, for decode scenarios.</summary>
        public string? OutputFormat { get; init; }

        /// <summary>Encoded size, for encode scenarios.</summary>
        public long? OutputBytes { get; init; }

        /// <summary>PSNR in dB of the encoded image decoded back, against the source, for lossy encodes.</summary>
        public double? PsnrDb { get; init; }
    }

    /// <summary>How far the two backends' decoded pixels are apart, to show both did the same work.</summary>
    public sealed class OutputComparison
    {
        public required string Scenario { get; init; }
        public required string Fixture { get; init; }
        public required int MaxChannelDifference { get; init; }
        public required double MeanAbsoluteDifference { get; init; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(BenchmarkReport))]
    [JsonSerializable(typeof(List<FixtureInfo>))]
    public sealed partial class BenchmarkJsonContext : JsonSerializerContext
    {
    }
}
