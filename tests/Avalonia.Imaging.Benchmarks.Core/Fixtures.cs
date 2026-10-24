using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>An encoded input both backends decode, with what produced it.</summary>
    public sealed record Fixture(string Name, string Format, string Content, int Width, int Height, string Encoder,
        byte[] Data)
    {
        public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Data));
    }

    /// <summary>
    /// Builds the fixture set from <see cref="SyntheticImages"/>. PixelMan encodes PNG, JPEG, BMP
    /// and GIF, deterministically and with every layout the suite needs (progressive JPEG,
    /// indexed and 16-bit PNG); Skia encodes WebP, which PixelMan does not write.
    /// </summary>
    public static class Fixtures
    {
        public const int PhotoWidth = 1920, PhotoHeight = 1080, LargeWidth = 3840, LargeHeight = 2160;

        public static IReadOnlyList<Fixture> Create(string? imageDirectory = null, Action<string>? log = null)
        {
            log ??= _ => { };
            log("generating source images");
            var large = SyntheticImages.Photo(LargeWidth, LargeHeight);
            var photo = SyntheticImages.Photo(PhotoWidth, PhotoHeight);
            var photoAlpha = SyntheticImages.Photo(PhotoWidth, PhotoHeight, alphaRamp: true);
            var uiIndices = SyntheticImages.UiIndices(PhotoWidth, PhotoHeight);
            var uiPalette = SyntheticImages.UiPalette();
            var ui = SyntheticImages.Expand(uiIndices, uiPalette);
            var palette = new BitmapPalette(Enumerable.Range(0, 64)
                .Select(i => Color.FromArgb(uiPalette[i * 4 + 3], uiPalette[i * 4 + 2], uiPalette[i * 4 + 1], uiPalette[i * 4]))
                .ToArray());

            var fixtures = new List<Fixture>();

            log("encoding fixtures");

            Fixture Encode(string name, string format, string content, int width, int height, PixelBuffer pixels,
                BitmapEncoder encoder, string encoderName = "PixelMan")
            {
                log(name);

                return EncodeCore(name, format, content, width, height, pixels, encoder, encoderName);
            }

            using (BenchmarkBackends.Bind(BenchmarkBackends.PixelMan.Create()))
            {
                fixtures.Add(Encode("photo-jpeg", "JPEG baseline q90 4:2:0", "photo", LargeWidth, LargeHeight,
                    Bgra(large, LargeWidth, LargeHeight), new JpegBitmapEncoder { Quality = 90 }));
                log("photo-jpeg-progressive");
                fixtures.Add(new Fixture("photo-jpeg-progressive", "JPEG progressive q90 4:2:0", "photo", LargeWidth,
                    LargeHeight, "PixelMan", ProgressiveJpeg(large, LargeWidth, LargeHeight)));
                fixtures.Add(Encode("photo-png", "PNG RGB 8-bit", "photo", PhotoWidth, PhotoHeight,
                    Rgb24(photo, PhotoWidth, PhotoHeight), new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("photo-png-alpha", "PNG RGBA 8-bit", "photo, alpha ramp", PhotoWidth, PhotoHeight,
                    Bgra(photoAlpha, PhotoWidth, PhotoHeight), new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("photo-png-16", "PNG RGBA 16-bit", "photo, alpha ramp", PhotoWidth, PhotoHeight,
                    PixelBuffer.Create(SyntheticImages.Rgba64(photoAlpha, PhotoWidth), new PixelSize(PhotoWidth, PhotoHeight),
                        PhotoWidth * 8, PixelFormats.Rgba64, AlphaFormat.Unpremul),
                    new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("gray-png", "PNG gray 8-bit", "photo luminance", PhotoWidth, PhotoHeight,
                    PixelBuffer.Create(SyntheticImages.Gray(photo), new PixelSize(PhotoWidth, PhotoHeight), PhotoWidth,
                        PixelFormats.Gray8, AlphaFormat.Opaque),
                    new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("ui-png", "PNG RGB 8-bit", "ui", PhotoWidth, PhotoHeight,
                    Rgb24(ui, PhotoWidth, PhotoHeight), new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("ui-png-palette", "PNG indexed 8-bit", "ui, 64 colours", PhotoWidth, PhotoHeight,
                    Indexed(uiIndices, palette), new PngBitmapEncoder { CompressionLevel = 6 }));
                fixtures.Add(Encode("ui-gif", "GIF", "ui, 64 colours", PhotoWidth, PhotoHeight,
                    Indexed(uiIndices, palette), new GifBitmapEncoder()));
                fixtures.Add(Encode("photo-bmp", "BMP 24-bit", "photo", PhotoWidth, PhotoHeight,
                    Rgb24(photo, PhotoWidth, PhotoHeight), new BmpBitmapEncoder()));
            }

            using (BenchmarkBackends.Bind(BenchmarkBackends.Skia.Create()))
            {
                fixtures.Add(Encode("photo-webp", "WebP lossy q75", "photo", PhotoWidth, PhotoHeight,
                    Bgra(photo, PhotoWidth, PhotoHeight), new WebpBitmapEncoder { Quality = 75 }, "SkiaSharp"));
                fixtures.Add(Encode("ui-webp-lossless", "WebP lossless", "ui", PhotoWidth, PhotoHeight,
                    Bgra(ui, PhotoWidth, PhotoHeight), new WebpBitmapEncoder { Lossless = true }, "SkiaSharp"));
            }

            if (imageDirectory is not null)
                fixtures.AddRange(External(imageDirectory));

            return fixtures;
        }

        /// <summary>The source pixels of the encode scenarios: the photo, straight-alpha BGRA, opaque.</summary>
        public static byte[] EncodeSourcePixels() => SyntheticImages.Photo(PhotoWidth, PhotoHeight);

        public static PixelBuffer EncodeSource(byte[] pixels) => Bgra(pixels, PhotoWidth, PhotoHeight);

        public const string ManifestName = "fixtures.json", EncodeSourceName = "encode-source.bgra";

        /// <summary>
        /// Writes the fixtures and the encode source to <paramref name="directory"/>, for a host that
        /// cannot afford to generate them (the browser's interpreter takes minutes): a manifest with
        /// each fixture's hash, plus one file per fixture.
        /// </summary>
        public static void Save(IReadOnlyList<Fixture> fixtures, byte[] encodeSource, string directory)
        {
            Directory.CreateDirectory(directory);

            foreach (var fixture in fixtures)
                File.WriteAllBytes(Path.Combine(directory, FileName(fixture.Name)), fixture.Data);

            File.WriteAllBytes(Path.Combine(directory, EncodeSourceName), encodeSource);
            File.WriteAllText(Path.Combine(directory, ManifestName), System.Text.Json.JsonSerializer.Serialize(
                fixtures.Select(Describe).ToList(), BenchmarkJsonContext.Default.ListFixtureInfo));
        }

        /// <summary>
        /// Reads fixtures written by <see cref="Save"/> through <paramref name="read"/>, failing when
        /// a file's hash differs from the manifest, so every host decodes the same bytes.
        /// </summary>
        public static async System.Threading.Tasks.Task<(IReadOnlyList<Fixture> Fixtures, byte[] EncodeSource)> LoadAsync(
            Func<string, System.Threading.Tasks.Task<byte[]>> read)
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                System.Text.Encoding.UTF8.GetString(await read(ManifestName)), BenchmarkJsonContext.Default.ListFixtureInfo)
                ?? throw new FormatException("Empty fixture manifest.");
            var fixtures = new List<Fixture>();

            foreach (var info in manifest)
            {
                var fixture = new Fixture(info.Name, info.Format, info.Content, info.Width, info.Height, info.Encoder,
                    await read(FileName(info.Name)));

                if (fixture.Sha256 != info.Sha256)
                    throw new InvalidDataException($"{info.Name} does not match its manifest hash.");

                fixtures.Add(fixture);
            }

            return (fixtures, await read(EncodeSourceName));
        }

        public static FixtureInfo Describe(Fixture f) => new()
        {
            Name = f.Name, Format = f.Format, Content = f.Content, Width = f.Width, Height = f.Height,
            Bytes = f.Data.Length, Encoder = f.Encoder, Sha256 = f.Sha256,
        };

        private static string FileName(string fixture) => fixture.Replace(':', '_') + ".bin";

        private static IEnumerable<Fixture> External(string directory)
        {
            foreach (var path in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
            {
                var data = File.ReadAllBytes(path);
                BitmapImageInfo info;

                using (BenchmarkBackends.Bind(BenchmarkBackends.Skia.Create()))
                {
                    if (!BitmapDecoder.TryIdentify(new MemoryStream(data), out info))
                        continue;
                }

                yield return new Fixture("file:" + Path.GetFileName(path), info.FormatName, "external file",
                    info.PixelSize.Width, info.PixelSize.Height, "external", data);
            }
        }

        private static Fixture EncodeCore(string name, string format, string content, int width, int height,
            PixelBuffer pixels, BitmapEncoder encoder, string encoderName = "PixelMan")
        {
            using var stream = new MemoryStream();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });
            encoder.Save(stream);

            return new Fixture(name, format, content, width, height, encoderName, stream.ToArray());
        }

        private static byte[] ProgressiveJpeg(byte[] bgra, int width, int height)
        {
            using var frame = PmBitmapFrame.CreateFromCopy(PmPixelFormat.Bgra32, (uint)width, (uint)height,
                (nuint)(width * 4), bgra);
            using var stream = new MemoryStream();

            frame.Encode(stream, PmCodecKind.Jpeg, new PmEncodeOptions
            {
                JpegQuality = 90,
                JpegProgressive = true,
                JpegSubsampling = PmJpegSubsampling.S420,
            });

            return stream.ToArray();
        }

        private static PixelBuffer Bgra(byte[] bgra, int width, int height) =>
            PixelBuffer.Create(bgra, new PixelSize(width, height), width * 4, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        private static PixelBuffer Rgb24(byte[] bgra, int width, int height)
        {
            var rgb = new byte[width * height * 3];

            for (var i = 0; i < width * height; i++)
            {
                rgb[i * 3] = bgra[i * 4 + 2];
                rgb[i * 3 + 1] = bgra[i * 4 + 1];
                rgb[i * 3 + 2] = bgra[i * 4];
            }

            return PixelBuffer.Create(rgb, new PixelSize(width, height), width * 3, PixelFormats.Rgb24, AlphaFormat.Opaque);
        }

        private static PixelBuffer Indexed(byte[] indices, BitmapPalette palette) =>
            PixelBuffer.Create(indices, new PixelSize(PhotoWidth, PhotoHeight), PhotoWidth, PixelFormats.Indexed8,
                AlphaFormat.Opaque, palette: palette);
    }
}
