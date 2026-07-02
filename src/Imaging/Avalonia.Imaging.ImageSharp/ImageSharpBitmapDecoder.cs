using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ISImage = SixLabors.ImageSharp.Image;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// Decoder over an eagerly loaded ImageSharp image. Frames are random access; the
    /// image stays alive until both the decoder and every frame produced by it are
    /// disposed.
    /// </summary>
    internal sealed class ImageSharpBitmapDecoder : IBitmapDecoder, ISeekableFrameDecoder
    {
        private readonly ISImage _image;
        private readonly BitmapDecodeOptions? _options;
        private readonly object _lock = new();
        private int _outstandingFrames;
        private bool _disposeRequested;
        private bool _disposed;
        private int _cursor;

        public ImageSharpBitmapDecoder(ISImage image, ImageSharpBitmapCodecInfo codecInfo,
            BitmapImageInfo headerInfo, BitmapDecodeOptions? options,
            IBitmapMemoryAllocator allocator, bool usedDecodeScale)
        {
            _image = image;
            _options = options;
            Codec = codecInfo;
            Info = headerInfo;
            Allocator = allocator;
            UsedDecodeScale = usedDecodeScale;
            FrameCount = image.Frames.Count;
        }

        public IBitmapCodecInfo CodecInfo => Codec;

        public BitmapImageInfo Info { get; }

        /// <inheritdoc cref="ISeekableFrameDecoder.FrameCount" />
        public int FrameCount { get; }

        int? IBitmapDecoder.FrameCount => FrameCount;

        internal ImageSharpBitmapCodecInfo Codec { get; }

        internal ISImage Image => _image;

        internal IBitmapMemoryAllocator Allocator { get; }

        /// <summary>
        /// Gets the source size before the decode plan, as declared by the header.
        /// </summary>
        internal PixelSize NativeSize => Info.PixelSize;

        /// <summary>
        /// Gets whether the image was loaded with a decode-time target size, so it is
        /// already scaled relative to <see cref="NativeSize"/>.
        /// </summary>
        internal bool UsedDecodeScale { get; }

        public IBitmapFrameSource? ReadNextFrame()
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(ImageSharpBitmapDecoder));

                if (_cursor >= FrameCount)
                    return null;

                var frame = ImageSharpBitmapFrameSource.Create(this, _cursor, _options);

                _cursor++;
                _outstandingFrames++;

                return frame;
            }
        }

        public IBitmapFrameSource GetFrame(int index)
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(ImageSharpBitmapDecoder));

                if (index < 0 || index >= FrameCount)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var frame = ImageSharpBitmapFrameSource.Create(this, index, _options);

                _outstandingFrames++;

                return frame;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    return;

                _disposeRequested = true;

                if (_outstandingFrames == 0)
                    DisposeCore();
            }
        }

        internal void ReleaseFrame()
        {
            lock (_lock)
            {
                _outstandingFrames--;

                if (_outstandingFrames == 0 && _disposeRequested && !_disposed)
                    DisposeCore();
            }
        }

        private void DisposeCore()
        {
            _disposed = true;
            _image.Dispose();
        }
    }
}
