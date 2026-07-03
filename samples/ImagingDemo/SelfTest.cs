using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImagingDemo
{
    /// <summary>
    /// Headless verification of every imaging operation the window drives, run against
    /// whichever backend is active. Invoke with <c>--selftest</c> (optionally with
    /// <c>--imaging=imagesharp</c>); the process prints PASS/FAIL per operation and exits
    /// non-zero if anything failed. Useful as a CI smoke test that both backends honour
    /// the same feature set.
    /// </summary>
    internal static class SelfTest
    {
        public static int Run()
        {
            var backend = ImagingBackend.Current.Name;
            var failures = new List<string>();

            Console.WriteLine($"Universal Bitmap Infrastructure self-test - backend: {backend}");

            void Check(string name, Func<bool> op)
            {
                try
                {
                    if (op())
                    {
                        Console.WriteLine($"  PASS  {name}");
                    }
                    else
                    {
                        Console.WriteLine($"  FAIL  {name}");
                        failures.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                    failures.Add(name);
                }
            }

            // A PNG source built from a generated PixelBuffer (exercises managed authoring
            // + the direct encoder path).
            var gradient = SampleImages.CreateGradient(200, 120);
            var png = Encode(new PngBitmapEncoder(), gradient);

            Check("PixelBuffer.Create + PNG encode produced bytes", () => png.Length > 0);

            Check("Identify reports header facts without decoding", () =>
            {
                using var stream = new MemoryStream(png);
                var info = BitmapDecoder.Identify(stream);
                return info.FormatName == "PNG" && info.PixelSize == new PixelSize(200, 120);
            });

            Check("Decode plan: target size + Rgb565 target format", () =>
            {
                using var stream = new MemoryStream(png);
                using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions
                {
                    TargetSize = new PixelSize(100, 60),
                    TargetFormat = PixelFormats.Rgb565,
                    TargetAlphaFormat = AlphaFormat.Opaque,
                    Interpolation = BitmapInterpolationMode.HighQuality,
                });
                return bitmap.PixelSize == new PixelSize(100, 60);
            });

            Check("Decode plan: crop region", () =>
            {
                using var stream = new MemoryStream(png);
                using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions
                {
                    SourceRegion = new PixelRect(10, 10, 50, 40),
                    TargetFormat = PixelFormats.Bgra8888,
                    TargetAlphaFormat = AlphaFormat.Premul,
                });
                return bitmap.PixelSize == new PixelSize(50, 40);
            });

            Check("MaxPixels bomb guard rejects an oversized decode", () =>
            {
                using var stream = new MemoryStream(png);
                try
                {
                    using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions { MaxPixels = 1000 });
                    return false;
                }
                catch (Exception)
                {
                    return true;
                }
            });

            foreach (var (name, factory) in new (string, Func<BitmapEncoder>)[]
            {
                ("PNG", () => new PngBitmapEncoder()),
                ("JPEG", () => new JpegBitmapEncoder { Quality = 85 }),
                ("WebP", () => new WebpBitmapEncoder { Quality = 80 }),
            })
            {
                Check($"Encode {name} with rotate-90 + flip-H transform round-trips", () =>
                {
                    using var source = Decode(png, PixelFormats.Bgra8888, AlphaFormat.Premul);
                    var encoder = factory();
                    encoder.Transform = new BitmapTransform(BitmapRotation.Rotate90, FlipHorizontal: true, FlipVertical: false);

                    using var encoded = new MemoryStream();
                    source.Save(encoded, encoder);
                    if (encoded.Length == 0)
                        return false;

                    encoded.Position = 0;

                    // Re-decode to a render-ready format: a JPEG re-decodes to Rgb24 on
                    // ImageSharp, which the render backend cannot install.
                    using var back = Bitmap.Decode(encoded, new BitmapDecodeOptions
                    {
                        TargetFormat = PixelFormats.Bgra8888,
                        TargetAlphaFormat = AlphaFormat.Premul,
                    });

                    // Rotate 90 swaps the 200x120 source to 120x200.
                    return back.PixelSize == new PixelSize(120, 200);
                });
            }

            Check("Lossless PNG round-trip preserves pixels exactly", () =>
            {
                var before = DecodeBuffer(png, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
                var pngAgain = Encode(new PngBitmapEncoder(), before);
                var after = DecodeBuffer(pngAgain, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
                return PixelBuffersEqual(before, after);
            });

            Check("Save(Stream, encoder) leaves the encoder reusable", () =>
            {
                using var source = Decode(png, PixelFormats.Bgra8888, AlphaFormat.Premul);
                var encoder = new PngBitmapEncoder();

                using var first = new MemoryStream();
                source.Save(first, encoder);

                using var second = new MemoryStream();
                source.Save(second, encoder);

                return encoder.Frames.Count == 0 && first.Length > 0 && second.Length > 0;
            });

            Console.WriteLine(failures.Count == 0
                ? $"ALL PASS ({backend})"
                : $"{failures.Count} FAILED ({backend}): {string.Join(", ", failures)}");

            return failures.Count == 0 ? 0 : 1;
        }

        private static byte[] Encode(BitmapEncoder encoder, PixelBuffer pixels)
        {
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static Bitmap Decode(byte[] bytes, PixelFormat format, AlphaFormat alpha)
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.Decode(stream, new BitmapDecodeOptions { TargetFormat = format, TargetAlphaFormat = alpha });
        }

        private static PixelBuffer DecodeBuffer(byte[] bytes, PixelFormat format, AlphaFormat alpha)
        {
            using var stream = new MemoryStream(bytes);
            using var decoder = BitmapDecoder.Create(stream,
                new BitmapDecodeOptions { TargetFormat = format, TargetAlphaFormat = alpha });
            using var frame = decoder.ReadNextFrame()
                ?? throw new InvalidOperationException("The image produced no frame.");
            return frame.ToPixelBuffer();
        }

        private static bool PixelBuffersEqual(PixelBuffer a, PixelBuffer b)
        {
            if (a.Size != b.Size || !a.Format.Equals(b.Format))
                return false;

            var spanA = a.Pixels.Span;
            var spanB = b.Pixels.Span;
            var rowBytes = a.Size.Width * (a.Format.BitsPerPixel / 8);

            for (var y = 0; y < a.Size.Height; y++)
            {
                if (!spanA.Slice(y * a.Stride, rowBytes).SequenceEqual(spanB.Slice(y * b.Stride, rowBytes)))
                    return false;
            }

            return true;
        }
    }
}
