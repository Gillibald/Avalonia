using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Holds a bitmap image.
    /// </summary>
    public class Bitmap : IBitmap, IImageBrushSource
    {
        private readonly bool _isTranscoded;
        /// <summary>
        /// Creates a decoder over the stream through the active imaging backend, giving
        /// access to header-only facts, frames and codec capabilities.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="options">The decode plan applied to all frames of the decoder.</param>
        /// <param name="ownsStream">When true the decoder takes ownership of the stream.</param>
        public static BitmapDecoder CreateDecoder(Stream stream, BitmapDecodeOptions? options = null,
            bool ownsStream = false)
        {
            return BitmapDecoder.Create(stream, options, ownsStream);
        }

        /// <summary>
        /// Decodes the first frame of the stream with a decode plan applied, e.g. a
        /// target size, source region or target pixel format.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="options">The decode plan.</param>
        public static Bitmap Decode(Stream stream, BitmapDecodeOptions? options = null)
        {
            using var decoder = BitmapDecoder.Create(stream, options);
            using var frame = decoder.ReadNextFrame() ??
                throw new ArgumentException("Unable to load bitmap from provided data");

            return frame.ToBitmap();
        }

        /// <summary>
        /// Decodes the first frame of the stream on a background thread. The token flows
        /// into the decode where the active backend supports cooperative cancellation.
        /// </summary>
        public static Task<Bitmap> DecodeAsync(Stream stream, BitmapDecodeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveOptions = options ?? new BitmapDecodeOptions();

            if (cancellationToken.CanBeCanceled && !effectiveOptions.Cancellation.CanBeCanceled)
                effectiveOptions.Cancellation = cancellationToken;

            return Task.Run(() => Decode(stream, effectiveOptions), cancellationToken);
        }

        /// <summary>
        /// Loads a Bitmap from a stream and decodes at the desired width. Aspect ratio is maintained.
        /// This is more efficient than loading and then resizing.
        /// </summary>
        /// <param name="stream">The stream to read the bitmap from. This can be any supported image format.</param>
        /// <param name="width">The desired width of the resulting bitmap.</param>
        /// <param name="interpolationMode">The <see cref="BitmapInterpolationMode"/> to use should any scaling be required.</param>
        /// <returns>An instance of the <see cref="Bitmap"/> class.</returns>
        public static Bitmap DecodeToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new Bitmap(GetFactory().LoadBitmapToWidth(stream, width, interpolationMode));
        }

        /// <summary>
        /// Loads a Bitmap from a stream and decodes at the desired height. Aspect ratio is maintained.
        /// This is more efficient than loading and then resizing.
        /// </summary>
        /// <param name="stream">The stream to read the bitmap from. This can be any supported image format.</param>
        /// <param name="height">The desired height of the resulting bitmap.</param>
        /// <param name="interpolationMode">The <see cref="BitmapInterpolationMode"/> to use should any scaling be required.</param>
        /// <returns>An instance of the <see cref="Bitmap"/> class.</returns>
        public static Bitmap DecodeToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new Bitmap(GetFactory().LoadBitmapToHeight(stream, height, interpolationMode));
        }

        /// <summary>
        /// Creates a Bitmap scaled to a specified size from the current bitmap.
        /// </summary>        
        /// <param name="destinationSize">The destination size.</param>
        /// <param name="interpolationMode">The <see cref="BitmapInterpolationMode"/> to use should any scaling be required.</param>
        /// <returns>An instance of the <see cref="Bitmap"/> class.</returns>
        public Bitmap CreateScaledBitmap(PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new Bitmap(GetFactory().ResizeBitmap(PlatformImpl.Item, destinationSize, interpolationMode));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Bitmap"/> class.
        /// </summary>
        /// <param name="fileName">The filename of the bitmap.</param>
        public Bitmap(string fileName)
        {
            PlatformImpl = RefCountable.Create(GetFactory().LoadBitmap(fileName));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Bitmap"/> class.
        /// </summary>
        /// <param name="stream">The stream to read the bitmap from.</param>
        public Bitmap(Stream stream)
        {
            PlatformImpl = RefCountable.Create(GetFactory().LoadBitmap(stream));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Bitmap"/> class.
        /// </summary>
        /// <param name="impl">A platform-specific bitmap implementation.</param>
        /// <param name="metadata">The metadata carried over from the decoded frame.</param>
        internal Bitmap(IRef<IBitmapImpl> impl, BitmapMetadata? metadata = null)
        {
            PlatformImpl = impl.Clone();
            Metadata = metadata;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Bitmap"/> class.
        /// </summary>
        /// <param name="impl">A platform-specific bitmap implementation. Bitmap class takes the ownership.</param>
        protected Bitmap(IBitmapImpl impl)
        {
            PlatformImpl = RefCountable.Create(impl);
        }

        /// <inheritdoc/>
        public virtual void Dispose()
        {
            PlatformImpl.Dispose();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Bitmap"/> class.
        /// </summary>
        /// <param name="format">The pixel format.</param>
        /// <param name="alphaFormat">The alpha format.</param>
        /// <param name="data">The pointer to the source bytes.</param>
        /// <param name="size">The size of the bitmap in device pixels.</param>
        /// <param name="dpi">The DPI of the bitmap.</param>
        /// <param name="stride">The number of bytes per row.</param>
        public Bitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
        {
            var factory = GetFactory();
            if (factory.IsSupportedBitmapPixelFormat(format))
                PlatformImpl = RefCountable.Create(factory.LoadBitmap(format, alphaFormat, data, size, dpi, stride));
            else
            {
                using (var transcoded = new BitmapMemory(PixelFormat.Rgba8888, Platform.AlphaFormat.Unpremul, size))
                {
                    var transcodedAlphaFormat = format.HasAlpha ? alphaFormat : Platform.AlphaFormat.Opaque;

                    PixelFormatTranscoder.Transcode(
                        data,
                        size,
                        stride,
                        format,
                        alphaFormat,
                        transcoded.Address,
                        transcoded.RowBytes,
                        transcoded.Format,
                        transcodedAlphaFormat);

                    PlatformImpl = RefCountable.Create(factory.LoadBitmap(PixelFormat.Rgba8888, transcodedAlphaFormat,
                        transcoded.Address, size, dpi, transcoded.RowBytes));
                }

                _isTranscoded = true;
            }
        }

        /// <inheritdoc/>
        public Vector Dpi => PlatformImpl.Item.Dpi;

        /// <inheritdoc/>
        public Size Size => PlatformImpl.Item.PixelSize.ToSizeWithDpi(Dpi);

        /// <inheritdoc/>
        public PixelSize PixelSize => PlatformImpl.Item.PixelSize;

        /// <summary>
        /// Gets the platform-specific bitmap implementation.
        /// </summary>
        internal IRef<IBitmapImpl> PlatformImpl { get; }

        IRef<IBitmapImpl> IBitmap.PlatformImpl => PlatformImpl;

        /// <summary>
        /// Saves the bitmap to a file.
        /// </summary>
        /// <param name="fileName">The filename.</param>
        /// <param name="quality">
        /// The optional quality for compression. 
        /// The quality value is interpreted from 0 - 100. If quality is null the default quality 
        /// setting is applied.
        /// </param>
        public void Save(string fileName, int? quality = null)
        {
            PlatformImpl.Item.Save(fileName, quality);
        }

        /// <summary>
        /// Saves the bitmap to a stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="quality">
        /// The optional quality for compression.
        /// The quality value is interpreted from 0 - 100. If quality is null the default quality
        /// setting is applied.
        /// </param>
        public void Save(Stream stream, int? quality = null)
        {
            PlatformImpl.Item.Save(stream, quality);
        }

        /// <summary>
        /// Encodes this bitmap to the stream with a configured encoder, e.g.
        /// <c>new JpegBitmapEncoder { Quality = 92 }</c>. The bitmap is appended to the
        /// encoder's frames; encoding a format the active imaging backend cannot write
        /// fails fast naming the backend.
        /// </summary>
        public void Save(Stream stream, BitmapEncoder encoder)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));
            _ = encoder ?? throw new ArgumentNullException(nameof(encoder));

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = ToPixelBufferCore() });
            encoder.Save(stream);
        }

        /// <summary>
        /// Gets the metadata carried over from the decoded frame, or null when the image
        /// was not produced by a metadata-capable decode.
        /// </summary>
        public BitmapMetadata? Metadata { get; }

        private PixelBuffer ToPixelBufferCore()
        {
            var format = Format ?? PixelFormat.Bgra8888;
            var alphaFormat = AlphaFormat ?? Platform.AlphaFormat.Premul;
            var stride = (PixelSize.Width * format.BitsPerPixel + 7) / 8;
            var pixels = new byte[checked(stride * PixelSize.Height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                var target = new LockedFramebuffer(handle.AddrOfPinnedObject(), PixelSize,
                    stride, Dpi, format, alphaFormat, null);

                CopyPixels(target);
            }
            finally
            {
                handle.Free();
            }

            return PixelBuffer.TakeOwnership(pixels, PixelSize, stride, format, alphaFormat, Dpi);
        }

        public virtual PixelFormat? Format => (PlatformImpl.Item as IReadableBitmapImpl)?.Format;

        public virtual AlphaFormat? AlphaFormat => (PlatformImpl.Item as IReadableBitmapImpl)?.AlphaFormat;

        private PixelRect ValidateSourceRect(PixelRect sourceRect)
        {
            if ((sourceRect.Width <= 0 || sourceRect.Height <= 0) && (sourceRect.X != 0 || sourceRect.Y != 0))
                throw new ArgumentOutOfRangeException(nameof(sourceRect));

            if (sourceRect.X < 0 || sourceRect.Y < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceRect));

            if (sourceRect.Width <= 0)
                sourceRect = sourceRect.WithWidth(PixelSize.Width);
            if (sourceRect.Height <= 0)
                sourceRect = sourceRect.WithHeight(PixelSize.Height);

            if (sourceRect.Right > PixelSize.Width || sourceRect.Bottom > PixelSize.Height)
                throw new ArgumentOutOfRangeException(nameof(sourceRect));
            return sourceRect;
        }
        
        private protected unsafe void CopyPixelsCore(PixelRect sourceRect, IntPtr buffer, int bufferSize, int stride,
            ILockedFramebuffer fb)
        {
            if (Format == null)
                throw new NotSupportedException("CopyPixels is not supported for this bitmap type");

            sourceRect = ValidateSourceRect(sourceRect);

            int minStride = checked(((sourceRect.Width * fb.Format.BitsPerPixel) + 7) / 8);
            if (stride < minStride)
                throw new ArgumentOutOfRangeException(nameof(stride));

            var minBufferSize = stride * sourceRect.Height;
            if (minBufferSize > bufferSize)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            var offsetX = checked(((sourceRect.X * Format.Value.BitsPerPixel) + 7) / 8);

            for (var y = 0; y < sourceRect.Height; y++)
            {
                var srcAddress = fb.Address + fb.RowBytes * (sourceRect.Y + y) + offsetX;
                var dstAddress = buffer + stride * y;
                Unsafe.CopyBlock(dstAddress.ToPointer(), srcAddress.ToPointer(), (uint)minStride);
            }
        }

        public virtual void CopyPixels(PixelRect sourceRect, IntPtr buffer, int bufferSize, int stride)
        {
            if (
                Format == null
                || PlatformImpl.Item is not IReadableBitmapImpl readable
                || Format != readable.Format
            )
            {
                throw new NotSupportedException("CopyPixels is not supported for this bitmap type");
            }

            if (_isTranscoded)
                throw new NotSupportedException("CopyPixels is not supported for transcoded bitmaps");
            
            using (var fb = readable.Lock())
                CopyPixelsCore(sourceRect, buffer, bufferSize, stride, fb);
        }

        /// <summary>
        /// Copies pixels to the target buffer and transcodes the pixel and alpha format if needed.
        /// </summary>
        /// <param name="buffer">The target buffer.</param>
        /// <exception cref="NotSupportedException"></exception>
        public void CopyPixels(ILockedFramebuffer buffer)
        {
            if (PlatformImpl.Item is not IReadableBitmapImpl readable || readable.Format == null || readable.AlphaFormat == null)
            {
                // Since we can't read pixels from the bitmap, we need to render it to a compatible bitmap and read pixels from it.
                using var rtb = new RenderTargetBitmap(PixelSize);
                using (var ctx = rtb.CreateDrawingContext())
                    ctx.DrawImage(this, new Rect(rtb.Size));
                rtb.CopyPixels(buffer);

                return;
            }

            if (buffer.Format != readable.Format || buffer.AlphaFormat != readable.AlphaFormat)
            {
                using (var fb = readable.Lock())
                {
                    PixelFormatTranscoder.Transcode(
                        fb.Address,
                        fb.Size,
                        fb.RowBytes,
                        fb.Format,
                        fb.AlphaFormat,
                        buffer.Address, 
                        buffer.RowBytes,
                        buffer.Format,
                        buffer.AlphaFormat);
                }
            }
            else
            {
                using (var fb = readable.Lock())
                {
                    CopyPixelsCore(new PixelRect(fb.Size), buffer.Address, buffer.RowBytes * buffer.Size.Height, fb.RowBytes, fb);
                }
            }
        }

        /// <inheritdoc/>
        void IImage.Draw(
            DrawingContext context,
            Rect sourceRect,
            Rect destRect)
        {
            context.DrawBitmap(
                PlatformImpl,
                1,
                sourceRect,
                destRect);
        }

        private static IPlatformRenderInterface GetFactory()
        {
            return AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
        }

        IRef<IBitmapImpl>? IImageBrushSource.Bitmap
        {
            get
            {
                if (!PlatformImpl.IsAlive)
                    return null;
                return PlatformImpl;
            }
        }
    }
}
