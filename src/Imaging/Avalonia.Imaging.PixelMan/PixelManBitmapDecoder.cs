using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// A decoder over PixelMan's frame decoder. Every frame is decoded by PixelMan when it
    /// is read, and each frame source owns its decoded frame independently of the decoder.
    /// </summary>
    internal class PixelManBitmapDecoder : IBitmapDecoder, IMetadataStoreSource
    {
        private readonly PmFrameDecoder _decoder;
        private readonly Stream _stream;
        private readonly BitmapDecodeOptions? _options;
        private readonly PixelManFrameAllocator _allocator;
        private readonly PixelSize? _reducedFrom;
        private int _cursor;
        private int _decoderPosition;
        private bool _disposed;

        protected PixelManBitmapDecoder(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator,
            PixelSize? reducedFrom = null)
        {
            _reducedFrom = reducedFrom;
            _decoder = decoder;
            _stream = stream;
            _options = options;
            _allocator = allocator;
            Codec = codec;
            Info = info;
            Metadata = new PixelManMetadataStore(decoder.GetContainerMetadata(), codec.ContainerFormat, null, isReadOnly: true);
        }

        public IBitmapCodecInfo CodecInfo => Codec;

        public BitmapImageInfo Info { get; }

        public int? FrameCount => _decoder.FrameCount is { } count ? (int)count : null;

        /// <summary>Gets the file-level metadata: the GIF logical screen, application and comment blocks.</summary>
        public IBitmapMetadataStore Metadata { get; }

        protected PixelManCodecInfo Codec { get; }

        protected object Sync { get; } = new();

        /// <summary>
        /// Creates the decoder with the capabilities the codec has: random frame access when
        /// PixelMan knows the frame count, the loop count for GIF. <paramref name="reducedFrom"/> is
        /// the full stored size when PixelMan was asked to decode reduced.
        /// </summary>
        public static PixelManBitmapDecoder Create(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator,
            PixelSize? reducedFrom = null)
        {
            if (codec.Kind == PmCodecKind.Gif)
            {
                return decoder.FrameCount is not null
                    ? new PixelManGifDecoder(decoder, stream, codec, info, options, allocator)
                    : new PixelManSequentialGifDecoder(decoder, stream, codec, info, options, allocator);
            }

            if (codec.Kind == PmCodecKind.Webp && WebpAnimation.ReadLoopCount(decoder) is { } loops)
                return new PixelManWebpDecoder(decoder, stream, codec, info, options, allocator, loops);

            return decoder.FrameCount is not null && (codec.Capabilities & BitmapCodecCapabilities.MultiFrame) != 0
                ? new PixelManSeekableDecoder(decoder, stream, codec, info, options, allocator)
                : new PixelManBitmapDecoder(decoder, stream, codec, info, options, allocator, reducedFrom);
        }

        public IBitmapFrameSource? ReadNextFrame()
        {
            lock (Sync)
            {
                ThrowIfDisposed();

                // Random access moves PixelMan's decoder, so the cursor returns to its own
                // position before reading on.
                var frame = Decode(() =>
                {
                    if (_decoderPosition != _cursor)
                        _decoder.SeekToFrame((uint)_cursor, Token);

                    return _decoder.Next(Token);
                });

                if (frame is null)
                    return null;

                _cursor++;
                _decoderPosition = _cursor;

                return Wrap(frame);
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _decoder.Dispose();
                _stream.Dispose();
            }
        }

        protected IBitmapFrameSource ReadFrameAt(int index)
        {
            lock (Sync)
            {
                ThrowIfDisposed();

                if (index < 0 || FrameCount is { } count && index >= count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var frame = Decode(() =>
                {
                    _decoder.SeekToFrame((uint)index, Token);
                    return _decoder.Next(Token);
                });

                _decoderPosition = index + 1;

                return Wrap(frame) ?? throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private System.Threading.CancellationToken Token => _options?.CancellationToken ?? default;

        private IBitmapFrameSource? Wrap(PmBitmapFrame? frame)
        {
            if (frame is null)
                return null;

            try
            {
                return PixelManBitmapFrameSource.Create(frame, Codec, _options, _allocator, _reducedFrom);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private PmBitmapFrame? Decode(Func<PmBitmapFrame?> decode)
        {
            // Bounds peak native memory when many frames decode in parallel.
            using var slot = DecodeConcurrencyLimiter.Enter(Token);

            PmBitmapFrame? frame;

            try
            {
                frame = decode();
            }
            catch (Exception exception) when (PixelManFormats.Translate(exception) is var translated && translated != exception)
            {
                throw translated;
            }

            // Like WIC, PixelMan recovers what it can from truncated or corrupt pixel data and
            // flags it; the imaging contract treats such data as a failed decode unless the
            // caller opted into the recovered pixels.
            if (frame is not null && _options?.AllowRecoveredPixelData != true &&
                (frame.DecodeReport.Warnings & PmDecodeWarnings.PixelDataRecovered) != 0)
            {
                frame.Dispose();

                throw new InvalidDataException("The image data is truncated or corrupt.");
            }

            return frame;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PixelManBitmapDecoder));
        }
    }

    internal class PixelManSeekableDecoder : PixelManBitmapDecoder, ISeekableFrameDecoder
    {
        public PixelManSeekableDecoder(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator)
            : base(decoder, stream, codec, info, options, allocator)
        {
        }

        int ISeekableFrameDecoder.FrameCount => FrameCount ?? 0;

        public IBitmapFrameSource GetFrame(int index) => ReadFrameAt(index);
    }

    /// <summary>
    /// An animated WebP: PixelMan composites every frame onto the full canvas and reports the
    /// loop count from the ANIM chunk.
    /// </summary>
    internal sealed class PixelManWebpDecoder : PixelManSeekableDecoder, IAnimatedDecoder
    {
        public PixelManWebpDecoder(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator, int loopCount)
            : base(decoder, stream, codec, info, options, allocator)
        {
            LoopCount = loopCount;
        }

        public int LoopCount { get; }
    }

    /// <summary>
    /// A GIF whose frame count PixelMan knows, so frames can be read in any order.
    /// </summary>
    internal sealed class PixelManGifDecoder : PixelManSeekableDecoder, IAnimatedDecoder, IPaletteDecoder
    {
        public PixelManGifDecoder(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator)
            : base(decoder, stream, codec, info, options, allocator)
        {
            LoopCount = GifDecoderInfo.ReadLoopCount(Metadata);
            GlobalPalette = GifDecoderInfo.ReadGlobalPalette(decoder);
        }

        public int LoopCount { get; }

        public BitmapPalette? GlobalPalette { get; }
    }

    /// <summary>
    /// A GIF read in order, when PixelMan does not know the frame count up front.
    /// </summary>
    internal sealed class PixelManSequentialGifDecoder : PixelManBitmapDecoder, IAnimatedDecoder, IPaletteDecoder
    {
        public PixelManSequentialGifDecoder(PmFrameDecoder decoder, Stream stream, PixelManCodecInfo codec,
            BitmapImageInfo info, BitmapDecodeOptions? options, PixelManFrameAllocator allocator)
            : base(decoder, stream, codec, info, options, allocator)
        {
            LoopCount = GifDecoderInfo.ReadLoopCount(Metadata);
            GlobalPalette = GifDecoderInfo.ReadGlobalPalette(decoder);
        }

        public int LoopCount { get; }

        public BitmapPalette? GlobalPalette { get; }
    }

    internal static class GifDecoderInfo
    {
        // Without a NETSCAPE block a GIF plays once; zero means forever.
        public static int ReadLoopCount(IBitmapMetadataStore metadata) =>
            ((PixelManMetadataStore)metadata).Metadata.LoopCount ?? 1;

        public static BitmapPalette? ReadGlobalPalette(PmFrameDecoder decoder)
        {
            using var global = decoder.GetGlobalPalette();

            return global is null ? null : PixelManFormats.ToPalette(global);
        }
    }
}
