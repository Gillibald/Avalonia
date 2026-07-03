using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Decodes an image stream through the active imaging backend: header-only identify,
    /// a forward frame cursor, and capability discovery.
    /// </summary>
    public sealed class BitmapDecoder : IDisposable
    {
        private readonly IBitmapDecoder _impl;
        private BitmapCodecInfo? _codecInfo;
        private bool _framesEnumerated;
        private bool _disposed;

        private BitmapDecoder(IBitmapDecoder impl)
        {
            _impl = impl;
        }

        /// <summary>
        /// Reads header-only image facts without decoding pixels. Returns false when the
        /// active backend does not recognize the format.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="info">The header facts.</param>
        /// <param name="behavior">
        /// How a forward-only stream is treated. With the default,
        /// <see cref="IdentifyStreamBehavior.RequireSeekable"/>, a seekable stream is
        /// position-restored (identify is repeatable and consumes nothing) and a
        /// forward-only stream throws <see cref="ArgumentException"/>.
        /// <see cref="IdentifyStreamBehavior.ConsumePrefix"/> opts in to reading a
        /// bounded prefix from a forward-only stream instead - suited to sniff-only
        /// flows; when the image is decoded afterwards, create a decoder and read
        /// <see cref="Info"/> instead, which owns its data.
        /// </param>
        public static bool TryIdentify(Stream stream, out BitmapImageInfo info,
            IdentifyStreamBehavior behavior = IdentifyStreamBehavior.RequireSeekable)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            var backend = ImagingBackend.Current;

            if (!stream.CanSeek && behavior == IdentifyStreamBehavior.ConsumePrefix)
            {
                // The caller opted in to consumption: identify from a bounded prefix.
                // The backend SPI itself stays seekable-only; this is sugar over the
                // span overload.
                var prefix = Platform.Internal.ImagingStreamHelper.ReadPrefix(
                    stream, backend.IdentifyPrefixLength);

                return backend.TryIdentify(prefix, out info);
            }

            return backend.TryIdentify(stream, out info);
        }

        /// <summary>
        /// Reads header-only image facts from in-memory encoded data: a complete payload
        /// or a prefix of at least the active backend's identify prefix length. Returns
        /// false when the format is not recognized.
        /// </summary>
        public static bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info) =>
            ImagingBackend.Current.TryIdentify(data, out info);

        /// <summary>
        /// Reads header-only image facts without decoding pixels, throwing when the
        /// active backend does not recognize the format.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="behavior">
        /// How a forward-only stream is treated; see
        /// <see cref="TryIdentify(Stream, out BitmapImageInfo, IdentifyStreamBehavior)"/>.
        /// </param>
        public static BitmapImageInfo Identify(Stream stream,
            IdentifyStreamBehavior behavior = IdentifyStreamBehavior.RequireSeekable)
        {
            if (!TryIdentify(stream, out var info, behavior))
            {
                throw new NotSupportedException(
                    $"The '{ImagingBackend.Current.Name}' imaging backend does not recognize the image data.");
            }

            return info;
        }

        /// <summary>
        /// Creates a decoder over the stream, detecting the container format from its header.
        /// </summary>
        /// <param name="stream">The encoded image stream.</param>
        /// <param name="options">The decode plan applied to all frames of this decoder.</param>
        /// <param name="ownsStream">
        /// When true the decoder takes ownership of the stream and disposes it with itself.
        /// </param>
        /// <remarks>
        /// The decoder owns a stable encoded source from creation: unless the stream is
        /// seekable and owned (in which case it belongs to the decoder from this call
        /// on), the backend secures its own copy of the encoded data before returning,
        /// and the caller may dispose or reuse the stream immediately.
        /// </remarks>
        public static BitmapDecoder Create(Stream stream, BitmapDecodeOptions? options = null,
            bool ownsStream = false)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (options is JpegDecodeOptions { ScaleDenominator: { } denominator and not (2 or 4 or 8) })
            {
                throw new ArgumentException(
                    $"{nameof(JpegDecodeOptions)}.{nameof(JpegDecodeOptions.ScaleDenominator)} must be 2, 4 or 8; got {denominator}.",
                    nameof(options));
            }

            return new BitmapDecoder(ImagingBackend.Current.CreateDecoder(stream, ownsStream, options));
        }

        /// <summary>
        /// Creates a decoder on a background thread. The token flows into
        /// <see cref="BitmapDecodeOptions.CancellationToken"/> (of a copy of the options,
        /// so the caller's instance is never mutated) so backends that support
        /// cooperative cancellation observe it during decoding.
        /// </summary>
        public static Task<BitmapDecoder> CreateAsync(Stream stream, BitmapDecodeOptions? options = null,
            bool ownsStream = false, CancellationToken cancellationToken = default)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            var effectiveOptions = WithCancellation(options, cancellationToken);

            return Task.Run(() => Create(stream, effectiveOptions, ownsStream), cancellationToken);
        }

        /// <summary>
        /// Combines caller options with an async entry point's token, copying the options
        /// when a token has to be injected so the caller's instance stays untouched.
        /// </summary>
        internal static BitmapDecodeOptions? WithCancellation(BitmapDecodeOptions? options,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return options;

            if (options is null)
                return new BitmapDecodeOptions { CancellationToken = cancellationToken };

            if (options.CancellationToken.CanBeCanceled)
                return options;

            var copy = options.Clone();
            copy.CancellationToken = cancellationToken;

            return copy;
        }

        /// <summary>
        /// Gets the codec descriptor for the detected container format.
        /// </summary>
        public BitmapCodecInfo CodecInfo => _codecInfo ??= new BitmapCodecInfo(_impl.CodecInfo);

        /// <summary>
        /// Gets the source header facts (pre-plan container/first-frame values) without
        /// advancing the frame cursor or decoding pixels - the identify path for
        /// forward-only input.
        /// </summary>
        public BitmapImageInfo Info => _impl.Info;

        /// <summary>
        /// Gets the number of frames, or null while unknown for forward-only formats.
        /// </summary>
        public int? FrameCount => _impl.FrameCount;

        /// <summary>
        /// Gets whether frames can be accessed randomly through <see cref="GetFrame"/>.
        /// </summary>
        public bool CanSeekFrames => _impl is ISeekableFrameDecoder;

        /// <summary>
        /// Advances the frame cursor and returns the next frame, or null when exhausted.
        /// </summary>
        public BitmapFrame? ReadNextFrame()
        {
            ThrowIfDisposed();

            var frame = _impl.ReadNextFrame();

            return frame is null ? null : new BitmapFrame(frame);
        }

        /// <summary>
        /// Reads the remaining frames of the cursor. The sequence can be obtained once -
        /// it consumes the forward-only cursor; frames are yielded lazily and owned by
        /// the caller.
        /// </summary>
        public IEnumerable<BitmapFrame> ReadFrames()
        {
            ThrowIfDisposed();

            if (_framesEnumerated)
            {
                throw new InvalidOperationException(
                    $"{nameof(ReadFrames)} consumes a forward-only cursor and can be called once.");
            }

            _framesEnumerated = true;

            return EnumerateFrames();
        }

        /// <summary>
        /// Returns the frame at the given index. Requires <see cref="CanSeekFrames"/>.
        /// </summary>
        public BitmapFrame GetFrame(int index)
        {
            ThrowIfDisposed();

            if (_impl is not ISeekableFrameDecoder seekable)
            {
                throw new NotSupportedException(
                    "This decoder is a forward-only cursor; random frame access is not supported for the format.");
            }

            if (index < 0 || index >= seekable.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            return new BitmapFrame(seekable.GetFrame(index));
        }

        /// <summary>
        /// Gets the container-level metadata when the active backend exposes it.
        /// </summary>
        public bool TryGetMetadata([NotNullWhen(true)] out BitmapMetadata? metadata)
        {
            ThrowIfDisposed();

            if (_impl is IMetadataSource source)
            {
                metadata = source.Metadata;
                return true;
            }

            metadata = null;
            return false;
        }

        /// <summary>
        /// Gets whether the stream is an animation, and its loop count (zero loops forever).
        /// </summary>
        public bool IsAnimated(out int loopCount)
        {
            ThrowIfDisposed();

            if (_impl is IAnimatedDecoder animated)
            {
                loopCount = animated.LoopCount;
                return true;
            }

            loopCount = 0;
            return false;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _impl.Dispose();
        }

        private IEnumerable<BitmapFrame> EnumerateFrames()
        {
            while (ReadNextFrame() is { } frame)
                yield return frame;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BitmapDecoder));
        }
    }
}
