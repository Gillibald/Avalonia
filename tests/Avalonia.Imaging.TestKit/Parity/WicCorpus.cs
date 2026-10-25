using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Avalonia.Imaging.TestKit.Parity
{
    /// <summary>
    /// The WPF decode captures of the PixelMan corpus, located through the
    /// <c>PIXELMAN_CORPUS</c> environment variable, which names the PixelMan repository's
    /// <c>test-files</c> directory. The corpus is not part of this repository, so the parity
    /// tier only runs where it is available.
    /// </summary>
    public static class WicCorpus
    {
        public const string EnvironmentVariable = "PIXELMAN_CORPUS";

        private const string CaptureSuffix = ".decoded.raw";

        /// <summary>Gets the corpus root, or null when it is not configured or missing.</summary>
        public static string? Root =>
            Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } root &&
            Directory.Exists(Path.Combine(root, "dotnet", "wpf"))
                ? root
                : null;

        /// <summary>
        /// Enumerates the captures of one decode mode: <see cref="WicParityDataset.StoredFormat"/>
        /// pairs <c>dotnet/wpf/from-SOURCE/NAME.EXT</c> with WPF's decode in the stored format,
        /// <see cref="WicParityDataset.ConvertedFormat"/> pairs the same files with WPF's decode to
        /// its default format under <c>dotnet/wpf-default/SOURCE</c>.
        /// </summary>
        public static IReadOnlyList<WicParityCase> Cases(string root, WicParityDataset dataset)
        {
            var cases = new List<WicParityCase>();
            var dotnet = Path.Combine(root, "dotnet");

            if (dataset == WicParityDataset.StoredFormat)
            {
                foreach (var capture in Directory.EnumerateFiles(Path.Combine(dotnet, "wpf"), "*" + CaptureSuffix,
                             SearchOption.AllDirectories))
                {
                    var encoded = capture.Substring(0, capture.Length - CaptureSuffix.Length);

                    if (File.Exists(encoded))
                        cases.Add(new WicParityCase(Key(root, capture), encoded, capture));
                }
            }
            else
            {
                foreach (var source in Directory.EnumerateDirectories(Path.Combine(dotnet, "wpf-default")))
                {
                    // Captures of the generated sources sit under wpf/from-SOURCE, the edge cases under wpf/edge-cases.
                    var encodedDirectory = Path.Combine(dotnet, "wpf", "from-" + Path.GetFileName(source));

                    if (!Directory.Exists(encodedDirectory))
                        encodedDirectory = Path.Combine(dotnet, "wpf", Path.GetFileName(source));

                    foreach (var capture in Directory.EnumerateFiles(source, "*" + CaptureSuffix))
                    {
                        var name = Path.GetFileName(capture);
                        var encoded = Path.Combine(encodedDirectory, name.Substring(0, name.Length - CaptureSuffix.Length));

                        if (File.Exists(encoded))
                            cases.Add(new WicParityCase(Key(root, capture), encoded, capture));
                    }
                }
            }

            return cases.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Reads an encoded corpus file, inflating the gzip-compressed ones.</summary>
        public static byte[] ReadEncoded(string path)
        {
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(path);

            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var buffer = new MemoryStream();

            gzip.CopyTo(buffer);

            return buffer.ToArray();
        }

        private static string Key(string root, string path) =>
            Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    /// <summary>Which WPF decode a parity case was captured from.</summary>
    public enum WicParityDataset
    {
        /// <summary>The stored format: <c>BitmapCreateOptions.PreservePixelFormat</c>.</summary>
        StoredFormat,

        /// <summary>WPF's default decode (<c>BitmapCreateOptions.None</c>), which converts to its display formats.</summary>
        ConvertedFormat,
    }

    /// <summary>An encoded corpus file and the WPF capture of its first frame.</summary>
    public sealed record WicParityCase(string Key, string EncodedPath, string CapturePath);
}
