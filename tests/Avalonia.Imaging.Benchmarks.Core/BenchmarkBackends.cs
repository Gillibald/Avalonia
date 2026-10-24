using System;
using System.Collections.Generic;
using Avalonia.Imaging.PixelMan;
using Avalonia.Platform;
using Avalonia.Skia.Imaging;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>An imaging backend under test, created fresh for every scenario.</summary>
    public sealed record BenchmarkBackend(string Name, Func<IImagingBackend> Create);

    /// <summary>The backends the suite compares, and the scope that binds one of them.</summary>
    public static class BenchmarkBackends
    {
        public static BenchmarkBackend Skia { get; } = new("SkiaSharp", () => new SkiaImagingBackend());

        public static BenchmarkBackend PixelMan { get; } = new("PixelMan", () => new PixelManImagingBackend());

        public static IReadOnlyList<BenchmarkBackend> All { get; } = new[] { Skia, PixelMan };

        /// <summary>
        /// Binds Skia's render interface and <paramref name="backend"/> as the active imaging
        /// backend until the returned scope is disposed. The public BitmapDecoder and
        /// BitmapEncoder API then decodes through the backend, into the format Skia installs
        /// (its render interface's default), as an app's decodes do.
        /// </summary>
        public static IDisposable Bind(IImagingBackend backend)
        {
            var scope = AvaloniaLocator.EnterScope();

            Avalonia.Skia.SkiaPlatform.Initialize();
            ImagingBackend.Register(backend);

            return scope;
        }
    }
}
