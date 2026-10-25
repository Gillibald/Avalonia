using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Imaging.Benchmarks;
using Avalonia.Imaging.ImageSharp;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

// Each backend encodes PNG and JPEG, decodes them back through BitmapDecoder and installs them
// through Bitmap.Decode, under NativeAOT. The exit code is the number of failed checks.
const int Width = 48;
const int Height = 32;

var backends = new List<BenchmarkBackend>(BenchmarkBackends.All)
{
    new("ImageSharp", () => new ImageSharpImagingBackend()),
};
var source = new byte[Width * Height * 4];

for (var y = 0; y < Height; y++)
{
    for (var x = 0; x < Width; x++)
    {
        var offset = (y * Width + x) * 4;

        source[offset] = (byte)(x * 5);
        source[offset + 1] = (byte)(y * 7);
        source[offset + 2] = (byte)(x ^ y);
        source[offset + 3] = 255;
    }
}

var pixels = PixelBuffer.Create(source, new PixelSize(Width, Height), Width * 4, PixelFormats.Bgra8888, AlphaFormat.Opaque);
var failures = 0;

foreach (var backend in backends)
{
    using var scope = BenchmarkBackends.Bind(backend.Create());

    failures += Check(backend.Name, "PNG", new PngBitmapEncoder(), tolerance: 0);
    failures += Check(backend.Name, "JPEG", new JpegBitmapEncoder(), tolerance: 24);
}

Console.WriteLine(failures == 0 ? "All imaging backends passed." : $"{failures} checks failed.");
return failures;

int Check(string backend, string format, BitmapEncoder encoder, int tolerance)
{
    try
    {
        using var encoded = new MemoryStream();

        encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });
        encoder.Save(encoded);

        var decodeOptions = new BitmapDecodeOptions { TargetFormat = PixelFormats.Bgra8888, TargetAlphaFormat = AlphaFormat.Premul };

        encoded.Position = 0;

        using (var decoder = BitmapDecoder.Create(encoded, decodeOptions))
        using (var frame = decoder.ReadNextFrame() ?? throw new InvalidDataException("no frame"))
        using (var locked = frame.Lock())
        {
            var maxDifference = 0;
            var row = new byte[Width * 4];

            for (var y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(locked.Address + y * locked.RowBytes, row, 0, row.Length);

                for (var i = 0; i < row.Length; i++)
                    maxDifference = Math.Max(maxDifference, Math.Abs(row[i] - source[y * Width * 4 + i]));
            }

            if (frame.PixelSize != new PixelSize(Width, Height) || maxDifference > tolerance)
                throw new InvalidDataException($"decoded {frame.PixelSize}, max difference {maxDifference}");
        }

        encoded.Position = 0;

        using (var bitmap = Bitmap.Decode(encoded))
        {
            if (bitmap.PixelSize != new PixelSize(Width, Height))
                throw new InvalidDataException($"installed {bitmap.PixelSize}");
        }

        Console.WriteLine($"{backend,-10} {format,-4} ok ({encoded.Length} bytes)");
        return 0;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"{backend,-10} {format,-4} FAILED: {exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}
