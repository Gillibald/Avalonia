using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BenchmarkDotNet.Attributes;

namespace Avalonia.Benchmarks.Media.Imaging
{
    /// <summary>
    /// The software decode stage on a 4000x3000 Bgra8888 frame with unassociated alpha:
    /// thumbnail downscale (box), upscale (bilinear), and the EXIF rotations that read the
    /// source column by column.
    /// </summary>
    [MemoryDiagnoser]
    public unsafe class PixelPipelineBenchmarks
    {
        private const int Width = 4000;
        private const int Height = 3000;

        private IntPtr _source;
        private IntPtr _dest;
        private LockedFramebuffer _framebuffer = null!;

        public enum Operation
        {
            BoxTo256,
            BilinearCrop512To1024,
            Rotate90,
            FlipHorizontal,
        }

        [Params(Operation.BoxTo256, Operation.BilinearCrop512To1024, Operation.Rotate90, Operation.FlipHorizontal)]
        public Operation Kind { get; set; }

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

            new Random(1).NextBytes(new Span<byte>((void*)_source, bytes));

            _framebuffer = new LockedFramebuffer(_source, new PixelSize(Width, Height), Width * 4, new Vector(96, 96),
                PixelFormats.Bgra8888, AlphaFormat.Unpremul, null);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            NativeMemory.Free((void*)_source);
            NativeMemory.Free((void*)_dest);
        }

        [Benchmark]
        public void Run()
        {
            var plan = Kind switch
            {
                Operation.BoxTo256 => new FusedPlanExecution(new PixelRect(0, 0, Width, Height), new PixelSize(256, 192),
                    PixelFormats.Bgra8888, AlphaFormat.Premul, BitmapInterpolationMode.HighQuality),
                Operation.BilinearCrop512To1024 => new FusedPlanExecution(new PixelRect(0, 0, 512, 512), new PixelSize(1024, 1024),
                    PixelFormats.Bgra8888, AlphaFormat.Premul, BitmapInterpolationMode.HighQuality),
                Operation.Rotate90 => new FusedPlanExecution(new PixelRect(0, 0, Height, Width), new PixelSize(Height, Width),
                    PixelFormats.Bgra8888, AlphaFormat.Unpremul, BitmapInterpolationMode.HighQuality, PixelOrientation.Rotate90),
                _ => new FusedPlanExecution(new PixelRect(0, 0, Width, Height), new PixelSize(Width, Height),
                    PixelFormats.Bgra8888, AlphaFormat.Unpremul, BitmapInterpolationMode.HighQuality, PixelOrientation.FlipHorizontal),
            };

            FusedPixelPipeline.Run(_framebuffer, plan, _dest, plan.TargetSize.Width * 4);
        }
    }
}
