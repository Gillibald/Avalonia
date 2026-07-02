using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    /// <summary>
    /// Decoder over a single SKCodec. The codec and the encoded data stay alive until
    /// both the decoder and every frame produced by it are disposed.
    /// </summary>
    internal sealed class SkiaBitmapDecoder : IBitmapDecoder
    {
        private readonly SKCodec _codec;
        private readonly SKData _data;
        private readonly BitmapDecodeOptions? _options;
        private readonly object _lock = new();
        private int _outstandingFrames;
        private bool _disposeRequested;
        private bool _disposed;
        private int _cursor;

        public SkiaBitmapDecoder(SKCodec codec, SKData data, SkiaBitmapCodecInfo codecInfo,
            BitmapImageInfo headerInfo, BitmapDecodeOptions? options, IBitmapMemoryAllocator allocator)
        {
            _codec = codec;
            _data = data;
            _options = options;
            CodecInfo = codecInfo;
            Info = headerInfo;
            Allocator = allocator;
        }

        public IBitmapCodecInfo CodecInfo { get; }

        public BitmapImageInfo Info { get; }

        // Animated containers currently surface their first frame only.
        public int? FrameCount => 1;

        internal SKCodec Codec => _codec;

        internal IBitmapMemoryAllocator Allocator { get; }

        public IBitmapFrameSource? ReadNextFrame()
        {
            lock (_lock)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(SkiaBitmapDecoder));

                if (_cursor >= 1)
                    return null;

                _cursor++;
                _outstandingFrames++;

                return new SkiaBitmapFrameSource(this, _options);
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
            _codec.Dispose();
            _data.Dispose();
        }
    }
}
