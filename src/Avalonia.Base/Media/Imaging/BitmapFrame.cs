using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// A single decoded frame: the pixel source between encoded bytes and a realized
    /// render bitmap. Header data is available immediately; pixels are decoded on first
    /// access.
    /// </summary>
    /// <remarks>
    /// The three pixel outputs have distinct lifetimes: <see cref="Lock"/> is a transient
    /// zero-copy view, <see cref="ToPixelBuffer"/> is a durable managed snapshot, and
    /// <see cref="ToBitmap"/> realizes a render bitmap once and shares it - repeated
    /// calls and the render tree reference the same pixels, and re-rendering never
    /// re-decodes.
    /// </remarks>
    public sealed class BitmapFrame : IDisposable
    {
        private readonly IBitmapFrameSource _source;
        private readonly Guid _containerFormat;
        private readonly object _sync = new();
        private IRef<IBitmapImpl>? _realized;
        private BitmapFrame? _thumbnail;
        private bool _disposed;

        internal BitmapFrame(IBitmapFrameSource source, Guid containerFormat)
        {
            _source = source;
            _containerFormat = containerFormat;
        }

        /// <summary>
        /// Gets the frame size in device pixels, after the decode plan is applied.
        /// </summary>
        public PixelSize PixelSize => _source.PixelSize;

        /// <summary>
        /// Gets the frame DPI as stored in the file, or 96 when the format carries none.
        /// </summary>
        public Vector Dpi => _source.Dpi;

        /// <summary>
        /// Gets the pixel format the frame produces, after the decode plan is applied.
        /// </summary>
        public PixelFormat Format => _source.PixelFormat;

        /// <summary>
        /// Gets the alpha format the frame produces, after the decode plan is applied.
        /// </summary>
        public AlphaFormat AlphaFormat => _source.AlphaFormat;

        /// <summary>
        /// Gets the frame's metadata when the active backend exposes it.
        /// </summary>
        public bool TryGetMetadata([NotNullWhen(true)] out BitmapMetadata? metadata)
        {
            // A backend may expose the capability on every frame; a frame without any
            // values reports no metadata, as one without the capability does.
            if (_source is IMetadataStoreSource source && HasValues(source.Metadata))
            {
                metadata = new BitmapMetadata(source.Metadata, _containerFormat);
                return true;
            }

            metadata = null;
            return false;
        }

        /// <summary>
        /// Gets the ICC profile embedded for this frame, exactly as stored, when the file
        /// carries one and the active backend exposes it.
        /// </summary>
        public bool TryGetColorProfile(out ReadOnlyMemory<byte> profile)
        {
            if (_source is IColorProfileSource { IccProfile.IsEmpty: false } source)
            {
                profile = source.IccProfile;
                return true;
            }

            profile = default;
            return false;
        }

        private static bool HasValues(IBitmapMetadataStore store)
        {
            using var paths = store.EnumeratePaths().GetEnumerator();

            return paths.MoveNext();
        }

        /// <summary>
        /// Gets the frame's palette when it decodes from an indexed format.
        /// </summary>
        public bool TryGetPalette([NotNullWhen(true)] out BitmapPalette? palette)
        {
            if (_source is IIndexedFrame indexed)
            {
                palette = indexed.Palette;
                return true;
            }

            palette = null;
            return false;
        }

        /// <summary>
        /// Gets the frame's animation data when it is part of an animation.
        /// </summary>
        public bool TryGetAnimation(out BitmapFrameAnimation animation)
        {
            if (_source is IAnimatedFrame animated)
            {
                animation = new BitmapFrameAnimation(animated.Delay, animated.Disposal,
                    animated.Origin, animated.Blend);
                return true;
            }

            animation = default;
            return false;
        }

        /// <summary>
        /// Gets the embedded thumbnail when the file carries one. The thumbnail is owned
        /// by this frame - repeated calls return the same instance and it is disposed
        /// with this frame, so do not dispose it separately.
        /// </summary>
        public bool TryGetThumbnail([NotNullWhen(true)] out BitmapFrame? thumbnail)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                if (_thumbnail is null && _source is IThumbnailSource { Thumbnail: { } source })
                    _thumbnail = new BitmapFrame(source, _containerFormat);

                thumbnail = _thumbnail;
                return thumbnail is not null;
            }
        }

        /// <summary>
        /// Returns a transient zero-copy view over the frame's decoded pixels, decoding
        /// on the first call. Dispose the view to release it.
        /// </summary>
        public ILockedFramebuffer Lock()
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                return _source.Lock();
            }
        }

        /// <summary>
        /// Copies the frame's pixels, with its palette when it is indexed, into a durable
        /// managed snapshot.
        /// </summary>
        public PixelBuffer ToPixelBuffer()
        {
            TryGetPalette(out var palette);

            using var framebuffer = Lock();

            return PixelBuffer.CopyFrom(framebuffer, palette);
        }

        /// <summary>
        /// Converts a rectangle of the frame into caller memory, starting at the destination
        /// origin, decoding on the first call.
        /// </summary>
        /// <param name="sourceRect">The rectangle to copy, within the frame.</param>
        /// <param name="destination">The destination bytes.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        /// <remarks>
        /// Copying to the frame's own format and alpha format moves the bits unchanged.
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
        /// Converts a rectangle of the frame into caller memory, starting at the destination
        /// origin, decoding on the first call.
        /// </summary>
        /// <param name="sourceRect">The rectangle to copy, within the frame.</param>
        /// <param name="destination">The destination memory.</param>
        /// <param name="destinationSize">The number of bytes available at <paramref name="destination"/>.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        public void CopyPixels(PixelRect sourceRect, IntPtr destination, int destinationSize, int destinationStride,
            PixelFormat format, AlphaFormat alphaFormat, PixelConversionOptions? options = null)
        {
            TryGetPalette(out var palette);

            using var framebuffer = Lock();

            PixelOperations.CopyPixels(framebuffer, palette, sourceRect, destination, destinationSize,
                destinationStride, format, alphaFormat, options);
        }

        /// <summary>
        /// Realizes a render bitmap from the frame, installing its pixels zero-copy.
        /// The realized bitmap is cached: repeated calls share the same render bitmap
        /// and pixels, so re-rendering never re-decodes. Requires a render backend.
        /// </summary>
        public Bitmap ToBitmap()
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                _realized ??= RefCountable.Create(
                    AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>()
                        .LoadBitmap(_source.Lock()));

                TryGetMetadata(out var metadata);

                return new Bitmap(_realized, metadata);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _realized?.Dispose();
                _realized = null;
                _thumbnail?.Dispose();
                _thumbnail = null;
                _source.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BitmapFrame));
        }
    }
}
