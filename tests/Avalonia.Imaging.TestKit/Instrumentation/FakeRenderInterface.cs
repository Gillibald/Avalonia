using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Instrumentation
{
    /// <summary>
    /// A render interface stub for the imaging kit: it implements exactly the members
    /// the imaging layer uses - the zero-copy framebuffer install and the copying
    /// pixel upload - and throws for everything else. Installs are recorded into a
    /// <see cref="FakeRenderInstall"/> so tests can assert identity and lifetime.
    /// </summary>
    public sealed class FakeRenderInterface : IPlatformRenderInterface
    {
        private const string NotAvailable =
            "The imaging kit render interface only installs and reads bitmaps.";

        private readonly FakeRenderInstall _install;

        public FakeRenderInterface(FakeRenderInstall install)
        {
            _install = install ?? throw new ArgumentNullException(nameof(install));
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(ILockedFramebuffer framebuffer)
        {
            _ = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));

            _install.OnInstalled(framebuffer);

            return new FakeInstalledBitmap(_install, framebuffer);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data,
            PixelSize size, Vector dpi, int stride)
        {
            // The pointer is only valid for the duration of the call, so the pixels are
            // copied, like a real backend uploads them.
            return new FakeCopiedBitmap(format, alphaFormat, data, size, dpi, stride);
        }

        public bool IsSupportedBitmapPixelFormat(PixelFormat format) => true;

        public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;

        public PixelFormat DefaultPixelFormat => PixelFormats.Bgra8888;

        public bool SupportsIndividualRoundRects => false;

        public bool SupportsRegions => false;

        public IGeometryImpl CreateEllipseGeometry(Rect rect) => throw new NotSupportedException(NotAvailable);

        public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => throw new NotSupportedException(NotAvailable);

        public IGeometryImpl CreateRectangleGeometry(Rect rect) => throw new NotSupportedException(NotAvailable);

        public IStreamGeometryImpl CreateStreamGeometry() => throw new NotSupportedException(NotAvailable);

        public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children) =>
            throw new NotSupportedException(NotAvailable);

        public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2) =>
            throw new NotSupportedException(NotAvailable);

        public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun) => throw new NotSupportedException(NotAvailable);

        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi) =>
            throw new NotSupportedException(NotAvailable);

        public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat) =>
            throw new NotSupportedException(NotAvailable);

        public IBitmapImpl LoadBitmap(string fileName) => throw new NotSupportedException(NotAvailable);

        public IBitmapImpl LoadBitmap(Stream stream) => throw new NotSupportedException(NotAvailable);

        public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
            throw new NotSupportedException(NotAvailable);

        public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
            throw new NotSupportedException(NotAvailable);

        public IWriteableBitmapImpl LoadWriteableBitmap(string fileName) => throw new NotSupportedException(NotAvailable);

        public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream) => throw new NotSupportedException(NotAvailable);

        public IBitmapImpl LoadBitmapToWidth(Stream stream, int width,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
            throw new NotSupportedException(NotAvailable);

        public IBitmapImpl LoadBitmapToHeight(Stream stream, int height,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
            throw new NotSupportedException(NotAvailable);

        public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
            throw new NotSupportedException(NotAvailable);

        public IGlyphRunImpl CreateGlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin) =>
            throw new NotSupportedException(NotAvailable);

        public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? graphicsApiContext) =>
            throw new NotSupportedException(NotAvailable);

        public IPlatformRenderInterfaceRegion CreateRegion() => throw new NotSupportedException(NotAvailable);

        /// <summary>
        /// The bitmap a zero-copy install produces: it exposes the captured view's
        /// descriptor and memory (Lock returns a non-owning window over the same
        /// address) and disposes the captured view exactly once.
        /// </summary>
        private sealed class FakeInstalledBitmap : IBitmapImpl, IReadableBitmapImpl
        {
            private readonly FakeRenderInstall _install;
            private readonly ILockedFramebuffer _view;
            private int _disposed;

            public FakeInstalledBitmap(FakeRenderInstall install, ILockedFramebuffer view)
            {
                _install = install;
                _view = view;
            }

            public Vector Dpi => _view.Dpi;

            public PixelSize PixelSize => _view.Size;

            public int Version => 1;

            public PixelFormat? Format => _view.Format;

            public AlphaFormat? AlphaFormat => _view.AlphaFormat;

            public ILockedFramebuffer Lock() => new LockedFramebuffer(_view.Address, _view.Size,
                _view.RowBytes, _view.Dpi, _view.Format, _view.AlphaFormat, null);

            public void Save(string fileName, int? quality = null) => throw new NotSupportedException(NotAvailable);

            public void Save(Stream stream, int? quality = null) => throw new NotSupportedException(NotAvailable);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _view.Dispose();
                _install.OnReleased();
            }
        }

        /// <summary>
        /// The bitmap a pointer upload produces: an owned managed copy of the pixels,
        /// pinned per Lock.
        /// </summary>
        private sealed class FakeCopiedBitmap : IBitmapImpl, IReadableBitmapImpl
        {
            private readonly byte[] _pixels;
            private readonly PixelFormat _format;
            private readonly AlphaFormat _alphaFormat;
            private readonly PixelSize _size;
            private readonly Vector _dpi;
            private readonly int _stride;

            public FakeCopiedBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data,
                PixelSize size, Vector dpi, int stride)
            {
                _format = format;
                _alphaFormat = alphaFormat;
                _size = size;
                _dpi = dpi;
                _stride = stride;
                _pixels = new byte[checked(stride * size.Height)];

                Marshal.Copy(data, _pixels, 0, _pixels.Length);
            }

            public Vector Dpi => _dpi;

            public PixelSize PixelSize => _size;

            public int Version => 1;

            public PixelFormat? Format => _format;

            public AlphaFormat? AlphaFormat => _alphaFormat;

            public ILockedFramebuffer Lock()
            {
                var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);

                return new LockedFramebuffer(handle.AddrOfPinnedObject(), _size, _stride,
                    _dpi, _format, _alphaFormat, () => handle.Free());
            }

            public void Save(string fileName, int? quality = null) => throw new NotSupportedException(NotAvailable);

            public void Save(Stream stream, int? quality = null) => throw new NotSupportedException(NotAvailable);

            public void Dispose()
            {
            }
        }
    }
}
