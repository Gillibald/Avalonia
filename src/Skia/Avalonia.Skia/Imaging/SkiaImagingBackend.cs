using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    /// <summary>
    /// The SkiaSharp imaging backend, the default when no other backend is selected.
    /// </summary>
    public sealed class SkiaImagingBackend : IImagingBackend
    {
        // Large enough for every supported header, including JPEGs with a full 64 KiB
        // APP1 (EXIF) segment before the frame header.
        private const int IdentifyPrefixBytes = 256 * 1024;

        private readonly IBitmapMemoryAllocator? _allocator;

        /// <summary>
        /// Initializes the backend with the application-wide buffer allocator.
        /// </summary>
        public SkiaImagingBackend()
        {
        }

        /// <summary>
        /// Initializes the backend with a specific buffer allocator; null uses the
        /// application-wide one.
        /// </summary>
        public SkiaImagingBackend(IBitmapMemoryAllocator? allocator)
        {
            _allocator = allocator;
        }

        internal IBitmapMemoryAllocator Allocator =>
            _allocator ?? Avalonia.Platform.Internal.BitmapMemoryAllocator.Current;

        /// <inheritdoc />
        public string Name => "SkiaSharp";

        /// <inheritdoc />
        public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => SkiaCodecCatalog.All;

        /// <inheritdoc />
        public bool TryIdentify(Stream stream, out BitmapImageInfo info)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (stream.CanSeek)
            {
                var position = stream.Position;

                try
                {
                    var prefix = ImagingStreamPrefix.ReadPrefix(stream, IdentifyPrefixBytes);

                    return TryIdentifyPrefix(prefix, out info);
                }
                finally
                {
                    stream.Position = position;
                }
            }
            else
            {
                var prefix = ImagingStreamPrefix.ReadPrefix(stream, IdentifyPrefixBytes);

                ImagingStreamPrefix.RememberConsumed(stream, prefix);

                return TryIdentifyPrefix(prefix, out info);
            }
        }

        /// <inheritdoc />
        public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            var effectiveStream = ImagingStreamPrefix.ResolveForDecode(stream);

            // Skia needs the full encoded data with a known length; Stream.CopyTo also
            // handles streams that serve partial reads, which SKManagedStream does not.
            var data = ReadToSkData(effectiveStream);

            if (ownsStream)
                stream.Dispose();

            var codec = SKCodec.Create(data);

            if (codec is null)
            {
                data.Dispose();

                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not recognize the image data.");
            }

            try
            {
                var codecInfo = SkiaCodecCatalog.FromEncodedFormat(codec.EncodedFormat) ??
                    throw new NotSupportedException(
                        $"The '{Name}' imaging backend does not support decoding '{codec.EncodedFormat}' images.");

                if (options is JpegDecodeOptions && codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
                {
                    throw new ArgumentException(
                        $"{nameof(JpegDecodeOptions)} were supplied, but the stream contains a {codecInfo.FormatName} image.",
                        nameof(options));
                }

                var pixelCount = (long)codec.Info.Width * codec.Info.Height;
                var maxPixels = options?.EffectiveMaxPixels ?? ImagingOptions.Effective.DefaultMaxPixels;

                if (pixelCount > maxPixels)
                {
                    throw new InvalidOperationException(
                        $"The image is {codec.Info.Width}x{codec.Info.Height} ({pixelCount:N0} pixels), " +
                        $"exceeding the configured limit of {maxPixels:N0} pixels.");
                }

                return new SkiaBitmapDecoder(codec, data, codecInfo, options, Allocator);
            }
            catch
            {
                codec.Dispose();
                data.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public IBitmapEncoderImpl CreateEncoder(Guid containerFormat)
        {
            var codecInfo = SkiaCodecCatalog.FromContainerFormat(containerFormat);

            if (codecInfo is null || (codecInfo.Capabilities & BitmapCodecCapabilities.Encode) == 0)
            {
                var formatName = codecInfo?.FormatName ?? containerFormat.ToString();

                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not support encoding {formatName} images.");
            }

            return new SkiaBitmapEncoderImpl(codecInfo.EncodedFormat, Allocator);
        }

        private static bool TryIdentifyPrefix(byte[] prefix, out BitmapImageInfo info)
        {
            using var data = SKData.CreateCopy(prefix);
            using var codec = SKCodec.Create(data);

            if (codec is null)
            {
                info = default;
                return false;
            }

            var codecInfo = SkiaCodecCatalog.FromEncodedFormat(codec.EncodedFormat);

            if (codecInfo is null)
            {
                info = default;
                return false;
            }

            var skInfo = codec.Info;
            var isAnimatedContainer = codec.EncodedFormat is SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp;

            info = new BitmapImageInfo(
                codecInfo.FormatName,
                new PixelSize(skInfo.Width, skInfo.Height),
                Dpi: null,
                skInfo.ColorType.ToAvalonia(),
                isAnimatedContainer ? null : 1,
                skInfo.AlphaType != SKAlphaType.Opaque);

            return true;
        }

        private static SKData ReadToSkData(Stream stream)
        {
            if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
            {
                var remaining = segment.AsSpan((int)memory.Position);

                memory.Position = memory.Length;

                return SKData.CreateCopy(remaining);
            }

            using var buffered = new MemoryStream();

            stream.CopyTo(buffered);

            return SKData.CreateCopy(buffered.GetBuffer().AsSpan(0, (int)buffered.Length));
        }

    }
}
