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
            AlphaFormat alphaFormat, Vector dpi, BitmapPalette? palette)
        {
            _pixels = pixels;
            Size = size;
            Stride = stride;
            Format = format;
            AlphaFormat = alphaFormat;
            Dpi = dpi;
            Palette = palette;
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
        /// Gets the palette the pixels index into; set for indexed formats, null otherwise.
        /// </summary>
        public BitmapPalette? Palette { get; }

        /// <summary>
        /// Copies the pixels of a transient framebuffer view into a new immutable buffer.
        /// </summary>
        /// <param name="source">The framebuffer view to copy.</param>
        /// <param name="palette">The palette of an indexed framebuffer; required for indexed formats, ignored otherwise.</param>
        public static PixelBuffer CopyFrom(ILockedFramebuffer source, BitmapPalette? palette = null)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));

            palette = ValidatePalette(source.Format, palette);

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
                source.AlphaFormat, source.Dpi, palette);
        }

        /// <summary>
        /// Creates a buffer by copying raw pixel bytes, e.g. generated or computed
        /// content destined for an encoder. The input is copied, so the caller's buffer
        /// stays the caller's.
        /// </summary>
        /// <param name="pixels">
        /// The pixel bytes, row-major with <paramref name="stride"/> bytes per row. Must
        /// cover at least stride * (height - 1) plus one row of pixels; the last row's
        /// stride padding may be omitted.
        /// </param>
        /// <param name="size">The size in device pixels.</param>
        /// <param name="stride">The number of bytes per row, at least one row of pixels.</param>
        /// <param name="format">The pixel format.</param>
        /// <param name="alphaFormat">The alpha format.</param>
        /// <param name="dpi">The DPI; null uses 96.</param>
        /// <param name="palette">The palette the pixels index into; required for indexed formats, ignored otherwise.</param>
        public static PixelBuffer Create(ReadOnlySpan<byte> pixels, PixelSize size, int stride,
            PixelFormat format, AlphaFormat alphaFormat, Vector? dpi = null, BitmapPalette? palette = null)
        {
            if (size.Width <= 0 || size.Height <= 0)
                throw new ArgumentException("The size is empty.", nameof(size));

            palette = ValidatePalette(format, palette);

            var minStride = (size.Width * format.BitsPerPixel + 7) / 8;

            if (stride < minStride)
                throw new ArgumentException("The stride is smaller than one row of pixels.", nameof(stride));

            var readableBytes = checked(stride * (size.Height - 1) + minStride);

            if (pixels.Length < readableBytes)
            {
                throw new ArgumentException(
                    "The pixel data is smaller than stride * (height - 1) plus one row of pixels.",
                    nameof(pixels));
            }

            var copy = new byte[checked(stride * size.Height)];

            pixels.Slice(0, readableBytes).CopyTo(copy);

            return new PixelBuffer(copy, size, stride, format, alphaFormat, dpi ?? new Vector(96, 96), palette);
        }

        /// <summary>
        /// Wraps an existing byte array without copying. The array must not be mutated afterwards.
        /// </summary>
        internal static PixelBuffer TakeOwnership(byte[] pixels, PixelSize size, int stride,
            PixelFormat format, AlphaFormat alphaFormat, Vector dpi, BitmapPalette? palette = null)
        {
            if (pixels.Length < checked(stride * size.Height))
                throw new ArgumentException("The pixel array is smaller than stride * height.", nameof(pixels));

            return new PixelBuffer(pixels, size, stride, format, alphaFormat, dpi, format.IsIndexed ? palette : null);
        }

        private static BitmapPalette? ValidatePalette(PixelFormat format, BitmapPalette? palette)
        {
            if (!format.IsIndexed)
                return null;

            return palette ?? throw new ArgumentException($"An indexed format ({format}) needs a palette.", nameof(palette));
        }

        /// <summary>
        /// Converts a rectangle of this buffer into caller memory, starting at the destination
        /// origin.
        /// </summary>
        /// <param name="sourceRect">The rectangle to copy, within this buffer.</param>
        /// <param name="destination">The destination bytes.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        /// <remarks>
        /// Copying to this buffer's own format and alpha format moves the bits unchanged.
        /// Validation happens before any byte is written.
        /// </remarks>
        public unsafe void CopyPixels(PixelRect sourceRect, Span<byte> destination, int destinationStride,
            PixelFormat format, AlphaFormat alphaFormat, PixelConversionOptions? options = null)
        {
            fixed (byte* address = destination)
            {
                CopyPixels(sourceRect, (IntPtr)address, destination.Length, destinationStride, format, alphaFormat,
                    options);
            }
        }

        /// <summary>
        /// Converts a rectangle of this buffer into caller memory, starting at the destination
        /// origin.
        /// </summary>
        /// <param name="sourceRect">The rectangle to copy, within this buffer.</param>
        /// <param name="destination">The destination memory.</param>
        /// <param name="destinationSize">The number of bytes available at <paramref name="destination"/>.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        public void CopyPixels(PixelRect sourceRect, IntPtr destination, int destinationSize, int destinationStride,
            PixelFormat format, AlphaFormat alphaFormat, PixelConversionOptions? options = null)
        {
            using var framebuffer = Lock();

            PixelOperations.CopyPixels(framebuffer, Palette, sourceRect, destination, destinationSize,
                destinationStride, format, alphaFormat, options);
        }

        /// <summary>
        /// Returns a copy of this buffer converted to another format.
        /// </summary>
        /// <param name="format">The target pixel format.</param>
        /// <param name="alphaFormat">The target alpha format.</param>
        /// <param name="options">Palette and alpha threshold for an indexed target.</param>
        public PixelBuffer Convert(PixelFormat format, AlphaFormat alphaFormat, PixelConversionOptions? options = null)
        {
            var stride = PixelFormatArithmetic.GetMinRowBytes(format, Size.Width);
            var pixels = new byte[checked(stride * Size.Height)];

            CopyPixels(new PixelRect(Size), pixels, stride, format, alphaFormat, options);

            var palette = format.IsIndexed ? options?.Palette ?? Palette : null;

            return new PixelBuffer(pixels, Size, stride, format, alphaFormat, Dpi, palette);
        }

        /// <summary>
        /// Returns the pixels inside a rectangle, with this buffer's format, palette and DPI.
        /// </summary>
        /// <param name="rect">The rectangle to keep, within this buffer.</param>
        public PixelBuffer Crop(PixelRect rect)
        {
            var stride = PixelFormatArithmetic.GetMinRowBytes(Format, Math.Max(rect.Width, 1));
            var pixels = new byte[checked(stride * Math.Max(rect.Height, 1))];

            CopyPixels(rect, pixels, stride, Format, AlphaFormat);

            return new PixelBuffer(pixels, rect.Size, stride, Format, AlphaFormat, Dpi, Palette);
        }

        /// <summary>
        /// Returns this buffer rotated and flipped without changing any pixel value. The
        /// rotation applies first, then the horizontal flip, then the vertical flip; a
        /// quarter turn swaps the width and height and the two DPI axes.
        /// </summary>
        /// <param name="transform">The rotation and flips to apply.</param>
        public unsafe PixelBuffer Transform(BitmapTransform transform)
        {
            var orientation = PixelOrientations.FromTransform(transform);

            if (orientation == PixelOrientation.Normal)
                return this;

            var swap = PixelOrientations.Decompose(orientation).Swap;
            var size = swap ? new PixelSize(Size.Height, Size.Width) : Size;
            var stride = PixelFormatArithmetic.GetMinRowBytes(Format, size.Width);
            var pixels = new byte[checked(stride * size.Height)];

            using (var framebuffer = Lock())
            {
                fixed (byte* address = pixels)
                    PixelLayout.Orient(framebuffer, orientation, address, stride);
            }

            var dpi = swap ? new Vector(Dpi.Y, Dpi.X) : Dpi;

            return new PixelBuffer(pixels, size, stride, Format, AlphaFormat, dpi, Palette);
        }

        /// <summary>
        /// Returns this buffer resampled to another size, in the same format and DPI.
        /// </summary>
        /// <param name="size">The target size in device pixels.</param>
        /// <param name="interpolation">
        /// The filter: nearest for <see cref="BitmapInterpolationMode.None"/> and
        /// <see cref="BitmapInterpolationMode.LowQuality"/>, otherwise a box filter when
        /// shrinking and bilinear when growing.
        /// </param>
        /// <remarks>
        /// Indexed buffers resample in color and are matched back against their own palette.
        /// Formats with more than eight bits per channel are filtered at eight bits.
        /// </remarks>
        public unsafe PixelBuffer Scale(PixelSize size, BitmapInterpolationMode interpolation = BitmapInterpolationMode.HighQuality)
        {
            if (size.Width <= 0 || size.Height <= 0)
                throw new ArgumentException("The size is empty.", nameof(size));

            if (size == Size)
                return this;

            if (Format.IsIndexed)
            {
                return Convert(PixelFormat.Bgra8888, AlphaFormat.Unpremul)
                    .Scale(size, interpolation)
                    .Convert(Format, AlphaFormat, new PixelConversionOptions { Palette = Palette });
            }

            var stride = PixelFormatArithmetic.GetMinRowBytes(Format, size.Width);
            var pixels = new byte[checked(stride * size.Height)];
            var plan = new FusedPlanExecution(new PixelRect(Size), size, Format, AlphaFormat, interpolation);

            using (var framebuffer = Lock())
            {
                fixed (byte* address = pixels)
                    FusedPixelPipeline.Run(framebuffer, plan, (IntPtr)address, stride);
            }

            return new PixelBuffer(pixels, size, stride, Format, AlphaFormat, Dpi, null);
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
            // Render backends install colors, not palette indices.
            if (Format.IsIndexed)
                return Convert(PixelFormat.Bgra8888, AlphaFormat.Unpremul).ToBitmap();

            using var framebuffer = Lock();

            return new Bitmap(Format, AlphaFormat, framebuffer.Address, Size, Dpi, Stride);
        }
    }
}
