using System;
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
        private readonly object _sync = new();
        private IRef<IBitmapImpl>? _realized;
        private bool _disposed;

        internal BitmapFrame(IBitmapFrameSource source)
        {
            _source = source;
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
        public bool TryGetMetadata(out BitmapMetadata metadata)
        {
            if (_source is IMetadataSource source)
            {
                metadata = source.Metadata;
                return true;
            }

            metadata = null!;
            return false;
        }

        /// <summary>
        /// Gets the frame's palette when it decodes from an indexed format.
        /// </summary>
        public bool TryGetPalette(out BitmapPalette palette)
        {
            if (_source is IIndexedFrame indexed)
            {
                palette = indexed.Palette;
                return true;
            }

            palette = null!;
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
        /// Gets the embedded thumbnail when the file carries one.
        /// </summary>
        public bool TryGetThumbnail(out BitmapFrame thumbnail)
        {
            if (_source is IThumbnailSource { Thumbnail: { } source })
            {
                thumbnail = new BitmapFrame(source);
                return true;
            }

            thumbnail = null!;
            return false;
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
        /// Copies the frame's pixels into a durable managed snapshot.
        /// </summary>
        public PixelBuffer ToPixelBuffer()
        {
            using var framebuffer = Lock();

            return PixelBuffer.CopyFrom(framebuffer);
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
