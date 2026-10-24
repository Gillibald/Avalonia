using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BenchmarkDotNet.Attributes;

namespace Avalonia.Benchmarks.Media.Imaging
{
    /// <summary>
    /// Row conversions of a 1920x1080 frame through the core transcoder: the swizzles and
    /// expansions of the realize path and the two alpha conversions.
    /// </summary>
    [MemoryDiagnoser]
    public unsafe class PixelConversionBenchmarks
    {
        private const int Width = 1920;
        private const int Height = 1080;

        private IntPtr _source;
        private IntPtr _dest;

        public enum Conversion
        {
            Rgba8888ToBgra8888,
            Rgb24ToBgra8888,
            Bgr24ToRgba8888,
            Bgra8888ToRgb24,
            Premultiply,
            Unpremultiply,
        }

        [Params(Conversion.Rgba8888ToBgra8888, Conversion.Rgb24ToBgra8888, Conversion.Bgr24ToRgba8888,
            Conversion.Bgra8888ToRgb24, Conversion.Premultiply, Conversion.Unpremultiply)]
        public Conversion Pair { get; set; }

        /// <summary>Gets or sets whether the vector kernels run, to compare against the scalar code.</summary>
        [Params(false, true)]
        public bool Vectorized { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            PixelKernels.UseVectors = Vectorized;

            const int bytes = Width * Height * 4;

            _source = (IntPtr)NativeMemory.Alloc(bytes);
            _dest = (IntPtr)NativeMemory.Alloc(bytes);

            var random = new Random(1);
            var span = new Span<byte>((void*)_source, bytes);

            random.NextBytes(span);

            // Premultiplied input needs channels at or below alpha.
            if (Pair == Conversion.Unpremultiply)
            {
                for (var i = 0; i < bytes; i += 4)
                {
                    var alpha = span[i + 3];

                    span[i] = (byte)(span[i] * alpha / 255);
                    span[i + 1] = (byte)(span[i + 1] * alpha / 255);
                    span[i + 2] = (byte)(span[i + 2] * alpha / 255);
                }
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            NativeMemory.Free((void*)_source);
            NativeMemory.Free((void*)_dest);
        }

        [Benchmark]
        public void Transcode()
        {
            var size = new PixelSize(Width, Height);

            switch (Pair)
            {
                case Conversion.Rgba8888ToBgra8888:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 4, PixelFormats.Rgba8888, AlphaFormat.Premul,
                        _dest, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Premul);
                    break;
                case Conversion.Rgb24ToBgra8888:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 3, PixelFormats.Rgb24, AlphaFormat.Opaque,
                        _dest, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Premul);
                    break;
                case Conversion.Bgr24ToRgba8888:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 3, PixelFormats.Bgr24, AlphaFormat.Opaque,
                        _dest, Width * 4, PixelFormats.Rgba8888, AlphaFormat.Unpremul);
                    break;
                case Conversion.Bgra8888ToRgb24:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Opaque,
                        _dest, Width * 3, PixelFormats.Rgb24, AlphaFormat.Opaque);
                    break;
                case Conversion.Premultiply:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Unpremul,
                        _dest, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Premul);
                    break;
                case Conversion.Unpremultiply:
                    PixelFormatTranscoder.Transcode(_source, size, Width * 4, PixelFormats.Bgra8888, AlphaFormat.Premul,
                        _dest, Width * 4, PixelFormats.Rgba8888, AlphaFormat.Unpremul);
                    break;
            }
        }
    }
}
