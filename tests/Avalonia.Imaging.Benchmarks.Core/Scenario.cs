using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Imaging.Benchmarks
{
    public enum ScenarioKind
    {
        Decode,
        Encode,

        /// <summary>Not a like-for-like comparison: shows where the time goes.</summary>
        Diagnostic,
    }

    /// <summary>What one run produced: the output size, and the pixels or encoded size to check it.</summary>
    public sealed record ScenarioOutcome(int Width, int Height, string? Format, byte[]? Pixels, long? EncodedBytes,
        double? PsnrDb);

    /// <summary>
    /// One timed operation through the public imaging API, run against whichever backend is bound.
    /// Decodes use the default options, so both backends decode to the format Skia's render
    /// interface installs (Bgra8888 on Windows, Rgba8888 in the browser; premultiplied, or
    /// opaque for an opaque image): the work an app's decode does.
    /// </summary>
    public sealed class Scenario
    {
        private readonly Func<string, bool, ScenarioOutcome> _run;

        private Scenario(string id, string fixture, ScenarioKind kind, Func<string, bool, ScenarioOutcome> run)
        {
            Id = id;
            Fixture = fixture;
            Kind = kind;
            _run = run;
        }

        public string Id { get; }

        public string Fixture { get; }

        public ScenarioKind Kind { get; }

        /// <summary>Runs once for <paramref name="backend"/> without keeping anything, for the timed iterations.</summary>
        public void Run(string backend) => _run(backend, false);

        /// <summary>Runs once and keeps what is needed to check the output.</summary>
        public ScenarioOutcome RunAndCapture(string backend) => _run(backend, true);

        public static Scenario Decode(string id, Fixture fixture, PixelSize? targetSize) =>
            new(id, fixture.Name, ScenarioKind.Decode, (_, capture) =>
            {
                var options = targetSize is null ? null : new BitmapDecodeOptions { TargetSize = targetSize };

                using var decoder = BitmapDecoder.Create(new MemoryStream(fixture.Data, writable: false), options);
                using var frame = decoder.ReadNextFrame() ??
                    throw new InvalidOperationException($"{fixture.Name} has no frame.");
                using var pixels = frame.Lock();

                return new ScenarioOutcome(pixels.Size.Width, pixels.Size.Height,
                    pixels.Format + " " + pixels.AlphaFormat, capture ? Copy(pixels) : null, null, null);
            });

        public static Scenario Encode(string id, PixelBuffer source, Func<BitmapEncoder> createEncoder, bool lossy) =>
            new(id, "photo " + source.Size.Width + "x" + source.Size.Height, ScenarioKind.Encode, (_, capture) =>
            {
                using var stream = new MemoryStream();
                var encoder = createEncoder();

                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = source });
                encoder.Save(stream);

                double? psnr = null;

                if (capture && lossy)
                {
                    stream.Position = 0;
                    psnr = Psnr(source, stream);
                }

                return new ScenarioOutcome(source.Size.Width, source.Size.Height, null, null, stream.Length, psnr);
            });

        /// <summary>
        /// A diagnostic, not a like-for-like comparison: each library's own codec API decodes to
        /// its native format (SkiaSharp's SKCodec to the codec's colour type, PixelMan's Normal
        /// mode to the stored layout), with no Avalonia pipeline. Set against <see cref="Decode"/>
        /// it separates codec cost from the conversion and adapter cost.
        /// </summary>
        public static Scenario CodecOnly(Fixture fixture) =>
            new("decode-codec", fixture.Name, ScenarioKind.Diagnostic, (backend, _) =>
            {
                if (backend == BenchmarkBackends.Skia.Name)
                {
                    using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(fixture.Data, writable: false)) ??
                        throw new NotSupportedException($"SKCodec does not read {fixture.Name}.");
                    var info = codec.Info;
                    var buffer = Marshal.AllocHGlobal((nint)info.BytesSize64);

                    try
                    {
                        var result = codec.GetPixels(info, buffer);

                        if (result is not (SkiaSharp.SKCodecResult.Success or SkiaSharp.SKCodecResult.IncompleteInput))
                            throw new NotSupportedException($"SKCodec failed on {fixture.Name}: {result}.");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    return new ScenarioOutcome(info.Width, info.Height, info.ColorType + " " + info.AlphaType, null,
                        null, null);
                }

                using var frame = Avalonia.PixelMan.PmBitmapFrame.Decode(new MemoryStream(fixture.Data, writable: false));

                return new ScenarioOutcome((int)frame.Width, (int)frame.Height, frame.Format.ToString(), null, null, null);
            });

        private static byte[] Copy(ILockedFramebuffer pixels)
        {
            var rowBytes = (pixels.Size.Width * pixels.Format.BitsPerPixel + 7) / 8;
            var copy = new byte[rowBytes * pixels.Size.Height];

            for (var y = 0; y < pixels.Size.Height; y++)
                Marshal.Copy(pixels.Address + y * pixels.RowBytes, copy, y * rowBytes, rowBytes);

            return copy;
        }

        // The colour channels of the re-decoded image against the source; the photo is opaque,
        // so premultiplication does not change them.
        private static double Psnr(PixelBuffer source, Stream encoded)
        {
            // Decoded to straight BGRA, the source's layout, whatever the render format is.
            using var decoder = BitmapDecoder.Create(encoded, new BitmapDecodeOptions
            {
                TargetFormat = PixelFormats.Bgra8888,
                TargetAlphaFormat = AlphaFormat.Unpremul,
            });
            using var frame = decoder.ReadNextFrame()!;
            using var pixels = frame.Lock();

            var decoded = Copy(pixels);
            var original = new byte[decoded.Length];

            // The encode source is a tight Bgra8888 buffer, so its rows match the copy's.
            source.Pixels.Span.CopyTo(original);

            double squared = 0;
            long count = 0;

            for (var i = 0; i < decoded.Length; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    var d = decoded[i + c] - original[i + c];

                    squared += d * d;
                    count++;
                }
            }

            var mse = squared / count;

            return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }
    }
}
