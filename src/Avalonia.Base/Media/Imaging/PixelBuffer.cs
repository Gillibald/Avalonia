using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// An immutable, managed snapshot of decoded pixels with a complete descriptor.
    /// </summary>
    /// <remarks>
    /// This is the durable pixel currency: it is not disposable, holds no native memory
    /// and is safe to store in data models and to cross threads. The copy out of a
    /// transient <see cref="ILockedFramebuffer"/> is paid exactly once, in
    /// <see cref="CopyFrom"/>. For transient zero-copy access use
    /// <see cref="ILockedFramebuffer"/> directly.
    /// </remarks>
    public sealed class PixelBuffer
    {
        private readonly byte[] _pixels;

        private PixelBuffer(byte[] pixels, PixelSize size, int stride, PixelFormat format,
            AlphaFormat alphaFormat, Vector dpi)
        {
            _pixels = pixels;
            Size = size;
            Stride = stride;
            Format = format;
            AlphaFormat = alphaFormat;
            Dpi = dpi;
        }

        /// <summary>
        /// Gets the pixel bytes, row-major with <see cref="Stride"/> bytes per row.
        /// </summary>
        public ReadOnlyMemory<byte> Pixels => _pixels;

        /// <summary>
        /// Gets the size in device pixels.
        /// </summary>
        public PixelSize Size { get; }

        /// <summary>
        /// Gets the number of bytes per row.
        /// </summary>
        public int Stride { get; }

        /// <summary>
        /// Gets the pixel format.
        /// </summary>
        public PixelFormat Format { get; }

        /// <summary>
        /// Gets the alpha format.
        /// </summary>
        public AlphaFormat AlphaFormat { get; }

        /// <summary>
        /// Gets the DPI.
        /// </summary>
        public Vector Dpi { get; }

        /// <summary>
        /// Copies the pixels of a transient framebuffer view into a new immutable buffer.
        /// </summary>
        public static PixelBuffer CopyFrom(ILockedFramebuffer source)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));

            if (source.Address == IntPtr.Zero)
                throw new ArgumentException("The framebuffer has no address.", nameof(source));
            if (source.Size.Width <= 0 || source.Size.Height <= 0)
                throw new ArgumentException("The framebuffer has an empty size.", nameof(source));

            var minStride = (source.Size.Width * source.Format.BitsPerPixel + 7) / 8;

            if (source.RowBytes < minStride)
                throw new ArgumentException("The framebuffer stride is smaller than one row of pixels.", nameof(source));

            var byteCount = checked(source.RowBytes * source.Size.Height);
            var pixels = new byte[byteCount];

            // The last row of an interop framebuffer may be allocated without its stride
            // padding (stride * (height - 1) + one row of pixels), so only the bytes up
            // to the end of the last row's pixels are read; the padding stays zero.
            var readableBytes = checked(source.RowBytes * (source.Size.Height - 1) + minStride);

            Marshal.Copy(source.Address, pixels, 0, readableBytes);

            return new PixelBuffer(pixels, source.Size, source.RowBytes, source.Format,
                source.AlphaFormat, source.Dpi);
        }

        /// <summary>
        /// Wraps an existing byte array without copying. The array must not be mutated afterwards.
        /// </summary>
        internal static PixelBuffer TakeOwnership(byte[] pixels, PixelSize size, int stride,
            PixelFormat format, AlphaFormat alphaFormat, Vector dpi)
        {
            if (pixels.Length < checked(stride * size.Height))
                throw new ArgumentException("The pixel array is smaller than stride * height.", nameof(pixels));

            return new PixelBuffer(pixels, size, stride, format, alphaFormat, dpi);
        }

        /// <summary>
        /// Returns a transient view over the pixels (a brief pin) for an encoder,
        /// transcoder or render install. Dispose the view to unpin; disposing more than
        /// once is safe.
        /// </summary>
        public ILockedFramebuffer Lock() => new PinnedFramebuffer(this);

        private sealed class PinnedFramebuffer : ILockedFramebuffer
        {
            private readonly PixelBuffer _owner;
            private GCHandle _handle;
            private int _disposed;

            public PinnedFramebuffer(PixelBuffer owner)
            {
                _owner = owner;
                _handle = GCHandle.Alloc(owner._pixels, GCHandleType.Pinned);
            }

            public IntPtr Address => _disposed == 0
                ? _handle.AddrOfPinnedObject()
                : throw new ObjectDisposedException(nameof(ILockedFramebuffer));

            public PixelSize Size => _owner.Size;

            public int RowBytes => _owner.Stride;

            public Vector Dpi => _owner.Dpi;

            public PixelFormat Format => _owner.Format;

            public AlphaFormat AlphaFormat => _owner.AlphaFormat;

            // A render install disposes the view it is handed and callers may also
            // dispose defensively, so Dispose must be idempotent.
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _handle.Free();
            }
        }

        /// <summary>
        /// Realizes a render bitmap from this buffer via the active render backend.
        /// </summary>
        public Bitmap ToBitmap()
        {
            using var framebuffer = Lock();

            return new Bitmap(Format, AlphaFormat, framebuffer.Address, Size, Dpi, Stride);
        }
    }
}
