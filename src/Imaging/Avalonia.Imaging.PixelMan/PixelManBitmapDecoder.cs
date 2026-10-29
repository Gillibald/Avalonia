using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// A decoder over PixelMan's frame decoder. The image of a single-image codec (BMP, JPEG,
    /// PNG) is described from its headers when it is read and decodes when its pixels or
    /// metadata are first needed, as WIC defers; every other frame is decoded by PixelMan when
    /// it is read. Each frame source owns its decoded frame independently of the decoder, and a
    /// frame source still waiting to decode keeps PixelMan's decoder and the stream open after
    /// this decoder is disposed.
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
        // The decoder's own reference plus one per deferred frame source not yet decoded.
        private int _references = 1;

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

                if (FrameCount is { } count && _cursor >= count)
                    return null;

                if (TryDefer(_cursor) is { } deferred)
                {
                    _cursor++;
                    return deferred;
                }

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
                Release();
            }
        }

        protected IBitmapFrameSource ReadFrameAt(int index)
        {
            lock (Sync)
            {
                ThrowIfDisposed();

                if (index < 0 || FrameCount is { } count && index >= count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                if (TryDefer(index) is { } deferred)
                    return deferred;

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

        /// <summary>
        /// A frame source for frame <paramref name="index"/> described from the headers PixelMan
        /// peeks, or null when the frame has to decode to be described: frames of TIFF, ICO and
        /// WebP, and indexed BMP and PNG images, whose palette is part of the description. GIF
        /// frames peek with their palette.
        /// </summary>
        private IBitmapFrameSource? TryDefer(int index)
        {
            var gif = Codec.Kind == PmCodecKind.Gif;

            if (!gif && Codec.Kind != PmCodecKind.Bmp && Codec.Kind != PmCodecKind.Jpeg && Codec.Kind != PmCodecKind.Png)
                return null;

            PmFrameHeader? header;
            BitmapPalette? palette = null;

            try
            {
                if (_decoderPosition != index)
                {
                    _decoder.SeekToFrame((uint)index, Token);
                    _decoderPosition = index;
                }

                header = _decoder.PeekFrame();

                if (header is { } peeked && PixelManFormats.TryMap(peeked.Format, out var format, out _) &&
                    format.IsIndexed)
                {
                    if (!gif)
                        return null;

                    using var indices = _decoder.PeekPalette();

                    if (indices is null)
                        return null;

                    palette = PixelManFormats.ToPalette(indices);
                }
            }
            catch (Exception exception) when (PixelManFormats.Translate(exception) is var translated && translated != exception)
            {
                throw translated;
            }

            if (header is not { } facts)
                return null;

            _references++;

            try
            {
                var frame = new DeferredFrame(this, index);

                return gif
                    ? PixelManBitmapFrameSource.CreateDeferredGif(facts, frame, Codec, _options, _allocator, palette)
                    : PixelManBitmapFrameSource.CreateDeferred(facts, frame, Codec, _options, _allocator, _reducedFrom);
            }
            catch
            {
                Release();
                throw;
            }
        }

        // Decodes a deferred frame source's frame. The source's reference keeps PixelMan's
        // decoder open, so this runs after the decoder itself has been disposed too.
        private PmBitmapFrame DecodeDeferred(int index)
        {
            lock (Sync)
            {
                var frame = Decode(() =>
                {
                    if (_decoderPosition != index)
                        _decoder.SeekToFrame((uint)index, Token);

                    return _decoder.Next(Token);
                });

                _decoderPosition = index + 1;

                return frame ?? throw new InvalidDataException("The image has no frame to decode.");
            }
        }

        private void Release()
        {
            lock (Sync)
            {
                if (--_references > 0)
                    return;
            }

            _decoder.Dispose();
            _stream.Dispose();
        }

        /// <summary>A frame of this decoder that decodes on first use, holding the decoder open until then.</summary>
        internal sealed class DeferredFrame(PixelManBitmapDecoder decoder, int index)
        {
            private PixelManBitmapDecoder? _decoder = decoder;

            /// <summary>Decodes the frame and lets go of the decoder.</summary>
            public PmBitmapFrame Decode()
            {
                var owner = _decoder ?? throw new InvalidOperationException("The frame has already been decoded.");

                try
                {
                    return owner.DecodeDeferred(index);
                }
                finally
                {
                    _decoder = null;
                    owner.Release();
                }
            }

            /// <summary>Lets go of the decoder without decoding.</summary>
            public void Abandon()
            {
                var owner = _decoder;
                _decoder = null;
                owner?.Release();
            }
        }

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
