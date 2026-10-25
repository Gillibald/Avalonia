using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Imaging.TestKit.Contract;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Parity
{
    /// <summary>
    /// The WIC parity tier: decodes every file of the PixelMan corpus through the public API
    /// with <see cref="BitmapFormatProfile.Wic"/> and compares the first frame with WPF's
    /// capture of it, byte for byte. Each backend keeps a baseline of the cases it is known to
    /// decode differently; a case that differs and is not listed, or differs in another way,
    /// fails the tier, and so does a listed case that now matches. Set
    /// <c>AVALONIA_IMAGING_PARITY_BLESS=1</c> to rewrite the baseline from the current results.
    /// </summary>
    /// <remarks>
    /// The tier runs where the corpus is available (<see cref="WicCorpus.EnvironmentVariable"/>)
    /// and the fixture names a baseline directory; otherwise it is skipped.
    /// </remarks>
    public abstract class WicParityTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string BlessVariable = "AVALONIA_IMAGING_PARITY_BLESS";

        [Fact]
        public void Decode_StoredFormat_MatchesTheWicCapturesOrTheBaseline() =>
            RunTier(WicParityDataset.StoredFormat, "wic-stored-format.baseline.txt");

        [Fact]
        public void Decode_ConvertedFormat_MatchesTheWicCapturesOrTheBaseline() =>
            RunTier(WicParityDataset.ConvertedFormat, "wic-converted-format.baseline.txt");

        private void RunTier(WicParityDataset dataset, string baselineName)
        {
            var root = WicCorpus.Root;
            var baselineDirectory = Fixture.WicParityBaselineDirectory;

            if (root is null)
            {
                Assert.Skip($"The PixelMan corpus is not available; set {WicCorpus.EnvironmentVariable} to its test-files directory.");
                return;
            }

            if (baselineDirectory is null)
            {
                Assert.Skip("The fixture names no WIC parity baseline.");
                return;
            }

            var cases = WicCorpus.Cases(root, dataset);

            Assert.NotEmpty(cases);

            var results = new SortedDictionary<string, string>(StringComparer.Ordinal);

            using (LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall)))
            {
                foreach (var parityCase in cases)
                {
                    if (Compare(parityCase, dataset) is { } outcome)
                        results[parityCase.Key] = outcome;
                }
            }

            var baselinePath = Path.Combine(baselineDirectory, baselineName);

            if (Environment.GetEnvironmentVariable(BlessVariable) == "1")
            {
                WriteBaseline(baselinePath, dataset, cases.Count, results);
                return;
            }

            var baseline = ReadBaseline(baselinePath);
            var report = new StringBuilder();

            foreach (var (key, outcome) in results)
            {
                if (!baseline.TryGetValue(key, out var expected))
                    report.AppendLine($"new: {key}: {outcome}");
                else if (expected != outcome)
                    report.AppendLine($"changed: {key}: {expected} -> {outcome}");
            }

            foreach (var (key, expected) in baseline)
            {
                if (!results.ContainsKey(key))
                    report.AppendLine($"stale: {key}: {expected} (now matches, or is no longer in the corpus)");
            }

            Assert.True(report.Length == 0,
                $"{dataset}: {cases.Count} cases, {results.Count} differ from WIC; the baseline lists {baseline.Count}. " +
                $"Set {BlessVariable}=1 to accept the current results.{Environment.NewLine}{report}");
        }

        /// <summary>Decodes one case; returns null when it matches the capture, else how it differs.</summary>
        private static string? Compare(WicParityCase parityCase, WicParityDataset dataset)
        {
            var capture = WicCapture.Read(parityCase.CapturePath);

            if (capture.FormatName == "Default")
                return $"WIC format unnamed in WPF ({capture.BitsPerPixel} bpp)";

            if (!WicFormatNames.TryGet(capture.FormatName, out var expectedFormat, out var expectedAlpha))
                return $"capture format {capture.FormatName} has no Avalonia format";

            var options = new BitmapDecodeOptions
            {
                FormatProfile = BitmapFormatProfile.Wic,
                PreservePixelFormat = dataset == WicParityDataset.StoredFormat,
                // WPF's BitmapDecoder never applies EXIF orientation.
                RespectExifOrientation = false,
            };

            try
            {
                using var decoder = BitmapDecoder.Create(new MemoryStream(WicCorpus.ReadEncoded(parityCase.EncodedPath)), options);
                using var frame = decoder.ReadNextFrame();

                if (frame is null)
                    return "no frame";

                if (frame.Format != expectedFormat || (expectedAlpha is { } alpha && frame.AlphaFormat != alpha))
                    return $"format {frame.Format} {frame.AlphaFormat}, WIC {capture.FormatName}";

                if (frame.PixelSize.Width != capture.Width || frame.PixelSize.Height != capture.Height)
                    return $"size {frame.PixelSize.Width}x{frame.PixelSize.Height}, WIC {capture.Width}x{capture.Height}";

                if (ComparePalette(frame, capture) is { } palette)
                    return palette;

                if (ComparePixels(frame, capture) is { } pixels)
                    return pixels;

                if (Math.Abs(frame.Dpi.X - capture.DpiX) > 0.01 || Math.Abs(frame.Dpi.Y - capture.DpiY) > 0.01)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"dpi {frame.Dpi.X:0.###}x{frame.Dpi.Y:0.###}, WIC {capture.DpiX:0.###}x{capture.DpiY:0.###}");
                }

                return null;
            }
            catch (Exception exception)
            {
                return $"decode failed: {exception.GetType().Name}";
            }
        }

        private static string? ComparePalette(BitmapFrame frame, WicCapture capture)
        {
            if (!frame.Format.IsIndexed)
                return null;

            if (!frame.TryGetPalette(out var palette))
                return "no palette";

            // WIC captures list exactly the colours the frame's palette holds.
            if (palette.Colors.Count != capture.Palette.Length)
                return $"palette of {palette.Colors.Count} colours, WIC {capture.Palette.Length}";

            var differing = palette.Colors.Where((color, i) => color.ToUInt32() != capture.Palette[i]).Count();

            return differing == 0 ? null : $"palette: {differing} colours differ";
        }

        private static string? ComparePixels(BitmapFrame frame, WicCapture capture)
        {
            using var pixels = frame.Lock();

            var bitsPerRow = (long)capture.Width * frame.Format.BitsPerPixel;
            var wholeBytes = (int)(bitsPerRow / 8);
            var tailBits = (int)(bitsPerRow % 8);
            // Bits past the last pixel of a sub-byte row are padding and may hold anything.
            var tailMask = (byte)(0xFF << (8 - tailBits));
            var row = new byte[wholeBytes + (tailBits > 0 ? 1 : 0)];
            var maxDifference = 0;
            var differingBytes = 0L;
            // The fourth byte of Bgr32 is unused, and WIC leaves it undefined.
            var unusedByte = frame.Format == PixelFormats.Bgr32 ? 3 : -1;

            for (var y = 0; y < capture.Height; y++)
            {
                Marshal.Copy(pixels.Address + y * pixels.RowBytes, row, 0, row.Length);

                var expected = capture.Pixels.AsSpan(y * capture.Stride, row.Length);

                for (var x = 0; x < row.Length; x++)
                {
                    var actual = row[x];
                    var reference = expected[x];

                    if (x % 4 == unusedByte)
                        continue;

                    if (x == wholeBytes)
                    {
                        actual &= tailMask;
                        reference &= tailMask;
                    }

                    if (actual != reference)
                    {
                        differingBytes++;
                        maxDifference = Math.Max(maxDifference, Math.Abs(actual - reference));
                    }
                }
            }

            return differingBytes == 0 ? null : $"pixels: max byte difference {maxDifference}";
        }

        private static Dictionary<string, string> ReadBaseline(string path)
        {
            var baseline = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!File.Exists(path))
                return baseline;

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;

                var tab = line.IndexOf('\t');

                if (tab > 0)
                    baseline[line.Substring(0, tab)] = line.Substring(tab + 1);
            }

            return baseline;
        }

        private static void WriteBaseline(string path, WicParityDataset dataset, int caseCount,
            SortedDictionary<string, string> results)
        {
            var text = new StringBuilder();

            text.Append("# WIC parity baseline, ").Append(dataset).Append(": the corpus cases this backend decodes ")
                .Append("differently from WPF, ").Append(results.Count).Append(" of ").Append(caseCount).Append('.').Append('\n');
            text.Append("# Rewrite with AVALONIA_IMAGING_PARITY_BLESS=1; one case per line: corpus path, tab, difference.\n");

            foreach (var (key, outcome) in results)
                text.Append(key).Append('\t').Append(outcome).Append('\n');

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
        }
    }
}
