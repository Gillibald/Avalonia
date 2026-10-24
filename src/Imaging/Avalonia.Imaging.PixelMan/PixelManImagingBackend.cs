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
            var hasHeader = TryDescribe(source, origin, codec, out var info, out var storedOrientation);

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
            var decodeOptions = new PmDecodeOptions(GetDecodeMode(options)) { MaxPixels = (ulong)Math.Max(0, maxPixels) };
            var frames = new PixelManFrameAllocator(Allocator);

            decodeOptions.FrameAllocator = frames;

            PixelSize? reducedFrom = null;

            if (hasHeader && TryGetDecodeTarget(codec, info.PixelSize, storedOrientation, options, out var target))
            {
                decodeOptions.TargetSize = target;
                reducedFrom = info.PixelSize;
            }

            if (hasHeader && TryGetDecodeFormat(codec, storedOrientation, options, out var format))
                decodeOptions.TargetFormat = format;

            try
            {
                decoder = PmBitmapFrame.OpenFramesSeekable(source, codec.Kind, decodeOptions,
                    options?.CancellationToken ?? default);
            }
            catch (Exception exception) when (PixelManFormats.Translate(exception) is var translated && translated != exception)
            {
                throw translated;
            }

            if (!hasHeader)
                info = new BitmapImageInfo(codec.FormatName, default, null, null, decoder.FrameCount is { } count ? (int)count : null, null);

            return PixelManBitmapDecoder.Create(decoder, source, codec, info, options, frames, reducedFrom);
        }

        /// <summary>
        /// The PixelMan mode a decode runs in: the WIC profile maps to PixelMan's WPF modes,
        /// preserving to <see cref="PmCompatibilityMode.WpfPreservePixelFormat"/> and converting to
        /// <see cref="PmCompatibilityMode.Wpf"/>; everything else decodes to the codec's own layout.
        /// </summary>
        internal static PmCompatibilityMode GetDecodeMode(BitmapDecodeOptions? options) =>
            options?.FormatProfile != BitmapFormatProfile.Wic ? PmCompatibilityMode.Normal
            : options.PreservePixelFormat ? PmCompatibilityMode.WpfPreservePixelFormat
            : PmCompatibilityMode.Wpf;

        private static bool TryIdentifyCore(Stream stream, long origin, out BitmapImageInfo info)
        {
            info = default;

            return TryDetect(stream, origin, out var codec) && TryDescribe(stream, origin, codec, out info, out _);
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

        // Asks PixelMan to write the plan's 32-bit RGB target itself when the plan changes
        // nothing but the format: its decode transform fills the pool rental directly, which
        // the frame source then adopts, instead of the pipeline converting into a second
        // frame-sized buffer. Other formats, and plans that crop, scale or reorient, keep
        // PixelMan's own layout and convert in the pipeline. GIF frames stay indexed for
        // animation compositing.
        private static bool TryGetDecodeFormat(PixelManCodecInfo codec, ushort? storedOrientation,
            BitmapDecodeOptions? options, out PmPixelFormat format)
        {
            format = default;

            if (options is not { TargetFormat: { } target, TargetAlphaFormat: { } alpha, SourceRegion: null, TargetSize: null } ||
                (options.RespectExifOrientation && storedOrientation is { } value and > 1 and <= 8) ||
                (codec.Kind != PmCodecKind.Png && codec.Kind != PmCodecKind.Jpeg &&
                 codec.Kind != PmCodecKind.Bmp && codec.Kind != PmCodecKind.Tiff) ||
                PixelManFormats.ToPixelMan(target, alpha) is not { } requested ||
                (requested != PmPixelFormat.Pbgra32 && requested != PmPixelFormat.Bgra32 &&
                 requested != PmPixelFormat.Prgba32 && requested != PmPixelFormat.Rgba32))
            {
                return false;
            }

            format = requested;
            return true;
        }

        // Asks PixelMan to decode JPEG and PNG reduced towards the plan's target, so the
        // pipeline resamples from a frame near that size instead of the full image. The
        // target is in oriented space and PixelMan's in stored axes; a source region is in
        // full-size coordinates, so such a plan decodes at full size.
        private static bool TryGetDecodeTarget(PixelManCodecInfo codec, PixelSize fullSize, ushort? storedOrientation,
            BitmapDecodeOptions? options, out (uint Width, uint Height) target)
        {
            target = default;

            if (options?.TargetSize is not { } requested || options.SourceRegion is not null ||
                (codec.Kind != PmCodecKind.Jpeg && codec.Kind != PmCodecKind.Png))
            {
                return false;
            }

            var orientation = options.RespectExifOrientation && storedOrientation is { } value and >= 1 and <= 8
                ? (PixelOrientation)value
                : PixelOrientation.Normal;
            var oriented = FusedPixelPipeline.GetOrientedSize(fullSize, orientation);
            var size = PixelManBitmapFrameSource.ResolveTargetSize(requested, oriented);

            if (size.Width >= oriented.Width && size.Height >= oriented.Height)
                return false;

            target = oriented.Width == fullSize.Width && oriented.Height == fullSize.Height
                ? ((uint)size.Width, (uint)size.Height)
                : ((uint)size.Height, (uint)size.Width);
            return true;
        }

        // PixelMan's inspection reads the size, frame count and resolution from the headers
        // along with the stored layout, which maps to the format a Normal-mode decode produces.
        private static bool TryDescribe(Stream stream, long origin, PixelManCodecInfo codec, out BitmapImageInfo info,
            out ushort? orientation)
        {
            info = default;
            orientation = null;

            PmImageInspection inspection;

            try
            {
                stream.Position = origin;
                inspection = PmCodecKind.Inspect(stream);
            }
            catch (PmException)
            {
                return false;
            }

            if (inspection.Width == 0 || inspection.Height == 0)
                return false;

            var (format, hasAlpha) = DescribeLayout(codec, inspection.StoredLayout);

            orientation = inspection.Orientation;

            info = new BitmapImageInfo(codec.FormatName, new PixelSize((int)inspection.Width, (int)inspection.Height),
                inspection.Dpi is { } dpi ? new Vector(dpi.X, dpi.Y) : null, format,
                inspection.FrameCount is { } count ? (int)count : null, hasAlpha);
            return true;
        }

        private static (PixelFormat? Format, bool? HasAlpha) DescribeLayout(PixelManCodecInfo codec, string storedLayout)
        {
            var support = s_capabilities.Value.QueryDecoding(codec.Kind, storedLayout, PmCompatibilityMode.Normal);

            if (support.SelectedFormat is not { } selected)
                return (null, null);

            return (PixelManFormats.TryMap(selected, out var mapped, out _) ? mapped : null, selected.HasAlpha);
        }
    }
}
