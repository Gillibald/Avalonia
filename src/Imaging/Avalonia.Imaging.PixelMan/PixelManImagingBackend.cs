using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// The PixelMan imaging backend: native codecs that reproduce the Windows Imaging
    /// Component, selected with the UsePixelManImaging AppBuilder extension.
    /// </summary>
    /// <remarks>
    /// Frames decode into their stored layout (palette images stay indexed, 16-bit and
    /// CMYK images keep their depth and colour model).
    /// </remarks>
    public sealed partial class PixelManImagingBackend : IImagingBackend
    {
        // Large enough for every supported header, including JPEGs with a full 64 KiB
        // APP1 (EXIF) segment before the frame header.
        private const int IdentifyPrefixBytes = 256 * 1024;

        private static readonly Lazy<PmCapabilities> s_capabilities = new(PmCapabilities.Create);

        private readonly IBitmapMemoryAllocator? _allocator;

        /// <summary>
        /// Initializes the backend with the application-wide buffer allocator.
        /// </summary>
        public PixelManImagingBackend()
        {
        }

        /// <summary>
        /// Initializes the backend with a specific buffer allocator; null uses the
        /// application-wide one.
        /// </summary>
        public PixelManImagingBackend(IBitmapMemoryAllocator? allocator)
        {
            _allocator = allocator;
        }

        internal IBitmapMemoryAllocator Allocator =>
            _allocator ?? Avalonia.Platform.Internal.BitmapMemoryAllocator.Current;

        /// <inheritdoc />
        public string Name => PixelManFormats.BackendName;

        /// <inheritdoc />
        public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => PixelManCodecCatalog.All;

        /// <inheritdoc />
        public int IdentifyPrefixLength => IdentifyPrefixBytes;

        /// <inheritdoc />
        public bool TryIdentify(Stream stream, out BitmapImageInfo info)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (!stream.CanSeek)
            {
                throw new ArgumentException(
                    "Identify requires a seekable stream, because reading a forward-only stream would consume it. " +
                    "Create a decoder instead and read its Info, or identify from bytes.",
                    nameof(stream));
            }

            var position = stream.Position;

            try
            {
                return TryIdentifyCore(stream, position, out info);
            }
            finally
            {
                stream.Position = position;
            }
        }

        /// <inheritdoc />
        public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info)
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);

            return TryIdentifyCore(stream, 0, out info);
        }

        /// <inheritdoc />
        public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            // PixelMan reads the stream while frames decode, so the decoder needs a stable,
            // seekable source it owns: the caller's stream when it is seekable and handed
            // over, a copy of the remaining data otherwise.
            Stream source;

            if (stream.CanSeek && ownsStream)
            {
                source = stream;
            }
            else
            {
                source = new MemoryStream();
                stream.CopyTo(source);
                source.Position = 0;

                if (ownsStream)
                    stream.Dispose();
            }

            try
            {
                return CreateDecoderCore(source, options);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public IBitmapEncoderImpl CreateEncoder(Guid containerFormat)
        {
            var codec = PixelManCodecCatalog.FromContainerFormat(containerFormat);

            if (codec is null || (codec.Capabilities & BitmapCodecCapabilities.Encode) == 0)
            {
                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not support encoding {codec?.FormatName ?? containerFormat.ToString()} images.");
            }

            return new PixelManBitmapEncoderImpl(codec);
        }

        private IBitmapDecoder CreateDecoderCore(Stream source, BitmapDecodeOptions? options)
        {
            options?.CancellationToken.ThrowIfCancellationRequested();

            var origin = source.Position;

            if (!TryDetect(source, origin, out var codec))
                throw new NotSupportedException($"The '{Name}' imaging backend does not recognize the image data.");

            if (options is JpegDecodeOptions && codec.Kind != PmCodecKind.Jpeg)
            {
                throw new ArgumentException(
                    $"{nameof(JpegDecodeOptions)} were supplied, but the stream contains a {codec.FormatName} image.",
                    nameof(options));
            }

            var maxPixels = options?.EffectiveMaxPixels ?? ImagingOptions.Effective.DefaultMaxPixels;
            var hasHeader = TryDescribe(source, origin, codec, out var info);

            // Reject an oversized image on its header, before PixelMan allocates the frame.
            if (hasHeader && (long)info.PixelSize.Width * info.PixelSize.Height > maxPixels)
            {
                throw new InvalidOperationException(
                    $"The image is {info.PixelSize.Width}x{info.PixelSize.Height} " +
                    $"({(long)info.PixelSize.Width * info.PixelSize.Height:N0} pixels), " +
                    $"exceeding the configured limit of {maxPixels:N0} pixels.");
            }

            source.Position = origin;

            PmFrameDecoder decoder;

            try
            {
                decoder = PmBitmapFrame.OpenFramesSeekable(source, codec.Kind,
                    new PmDecodeOptions(PmCompatibilityMode.Normal) { MaxPixels = (ulong)Math.Max(0, maxPixels) },
                    options?.CancellationToken ?? default);
            }
            catch (Exception exception) when (PixelManFormats.Translate(exception) is var translated && translated != exception)
            {
                throw translated;
            }

            if (!hasHeader)
                info = new BitmapImageInfo(codec.FormatName, default, null, null, decoder.FrameCount is { } count ? (int)count : null, null);

            return PixelManBitmapDecoder.Create(decoder, source, codec, info, options, Allocator);
        }

        private static bool TryIdentifyCore(Stream stream, long origin, out BitmapImageInfo info)
        {
            info = default;

            return TryDetect(stream, origin, out var codec) && TryDescribe(stream, origin, codec, out info);
        }

        private static bool TryDetect(Stream stream, long origin, out PixelManCodecInfo codec)
        {
            codec = null!;

            Span<byte> prefix = stackalloc byte[64];

            stream.Position = origin;

            var read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);

            if (!PmCodecKind.TryDetect(prefix.Slice(0, read), out var kind) || PixelManCodecCatalog.FromKind(kind) is not { } found)
                return false;

            codec = found;
            return true;
        }

        // Size, resolution and frame count come from the headers; the stored pixel format
        // from PixelMan's layout inspection, mapped the way a Normal-mode decode maps it.
        private static bool TryDescribe(Stream stream, long origin, PixelManCodecInfo codec, out BitmapImageInfo info)
        {
            info = default;

            if (!PixelManHeaderReader.TryRead(codec.Kind, PixelManHeaderReader.FromStream(stream, origin), out var header))
                return false;

            PixelFormat? format = null;
            bool? hasAlpha = null;

            try
            {
                stream.Position = origin;

                var inspection = PmCodecKind.Inspect(stream);
                var support = s_capabilities.Value.QueryDecoding(codec.Kind, inspection.StoredLayout, PmCompatibilityMode.Normal);

                if (support.SelectedFormat is { } selected)
                {
                    hasAlpha = selected.HasAlpha;

                    if (PixelManFormats.TryMap(selected, out var mapped, out _))
                        format = mapped;
                }
            }
            catch (PmException)
            {
                // The layout is optional; the header facts stand on their own.
            }

            info = new BitmapImageInfo(codec.FormatName, header.Size, header.Dpi, format, header.FrameCount, hasAlpha);
            return true;
        }
    }
}
