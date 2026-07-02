using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using ISImage = SixLabors.ImageSharp.Image;
using ISSize = SixLabors.ImageSharp.Size;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// The SixLabors.ImageSharp imaging backend: a fully managed codec set selected with
    /// the UseImageSharpImaging AppBuilder extension.
    /// </summary>
    public sealed class ImageSharpImagingBackend : IImagingBackend
    {
        // Large enough for every supported header, including JPEGs with a full 64 KiB
        // APP1 (EXIF) segment before the frame header.
        private const int IdentifyPrefixBytes = 256 * 1024;

        // The backend works on its own clone of the six core format configurations;
        // Configuration.Default is never touched.
        private static readonly Configuration s_configuration = CreateConfiguration();

        private readonly IBitmapMemoryAllocator? _allocator;

        /// <summary>
        /// Initializes the backend with the application-wide buffer allocator.
        /// </summary>
        public ImageSharpImagingBackend()
        {
        }

        /// <summary>
        /// Initializes the backend with a specific buffer allocator; null uses the
        /// application-wide one.
        /// </summary>
        public ImageSharpImagingBackend(IBitmapMemoryAllocator? allocator)
        {
            _allocator = allocator;
        }

        internal IBitmapMemoryAllocator Allocator =>
            _allocator ?? Avalonia.Platform.Internal.BitmapMemoryAllocator.Current;

        internal static Configuration ImagingConfiguration => s_configuration;

        /// <inheritdoc />
        public string Name => "ImageSharp";

        /// <inheritdoc />
        public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs => ImageSharpCodecCatalog.All;

        /// <inheritdoc />
        public bool TryIdentify(Stream stream, out BitmapImageInfo info)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            if (stream.CanSeek)
            {
                var position = stream.Position;

                try
                {
                    return TryIdentifyCore(stream, out info);
                }
                finally
                {
                    stream.Position = position;
                }
            }

            var prefix = ImagingStreamPrefix.ReadPrefix(stream, IdentifyPrefixBytes);

            ImagingStreamPrefix.RememberConsumed(stream, prefix);

            using var prefixStream = new MemoryStream(prefix, writable: false);

            return TryIdentifyCore(prefixStream, out info);
        }

        /// <inheritdoc />
        public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            var effectiveStream = ImagingStreamPrefix.ResolveForDecode(stream);

            // ImageSharp seeks while decoding; buffering the encoded bytes also covers
            // partial reads and lets the input stream go as soon as it is drained.
            using var buffered = ReadToMemory(effectiveStream);

            if (ownsStream)
                stream.Dispose();

            ImageInfo imageInfo;

            try
            {
                imageInfo = ISImage.Identify(new DecoderOptions { Configuration = s_configuration }, buffered);
            }
            catch (UnknownImageFormatException)
            {
                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not recognize the image data.");
            }

            var codecInfo = ImageSharpCodecCatalog.FromImageFormat(imageInfo.Metadata.DecodedImageFormat) ??
                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not support decoding " +
                    $"'{imageInfo.Metadata.DecodedImageFormat?.Name}' images.");

            if (options is JpegDecodeOptions && codecInfo.ContainerFormat != BitmapContainerFormats.Jpeg)
            {
                throw new ArgumentException(
                    $"{nameof(JpegDecodeOptions)} were supplied, but the stream contains a {codecInfo.FormatName} image.",
                    nameof(options));
            }

            // ImageSharp materializes every pixel in Load, so the decompression-bomb
            // guard must run on the header facts, before any frame memory exists.
            var pixelCount = (long)imageInfo.Width * imageInfo.Height;
            var maxPixels = options?.EffectiveMaxPixels ?? ImagingOptions.Effective.DefaultMaxPixels;

            if (pixelCount > maxPixels)
            {
                throw new InvalidOperationException(
                    $"The image is {imageInfo.Width}x{imageInfo.Height} ({pixelCount:N0} pixels), " +
                    $"exceeding the configured limit of {maxPixels:N0} pixels.");
            }

            var nativeSize = new PixelSize(imageInfo.Width, imageInfo.Height);
            var fusedTarget = ResolveFusedTarget(codecInfo, options, nativeSize);

            var decoderOptions = new DecoderOptions
            {
                Configuration = s_configuration,
                MaxFrames = (codecInfo.Capabilities & BitmapCodecCapabilities.MultiFrame) != 0 ? uint.MaxValue : 1,
                TargetSize = fusedTarget is { } target ? new ISSize(target.Width, target.Height) : null,
            };

            buffered.Position = 0;

            var image = ISImage.Load(decoderOptions, buffered);

            try
            {
                var usedDecodeScale = fusedTarget is not null &&
                    (image.Width != nativeSize.Width || image.Height != nativeSize.Height);

                return new ImageSharpBitmapDecoder(image, codecInfo, options, Allocator, nativeSize, usedDecodeScale);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public IBitmapEncoderImpl CreateEncoder(Guid containerFormat)
        {
            var codecInfo = ImageSharpCodecCatalog.FromContainerFormat(containerFormat);

            if (codecInfo is null || (codecInfo.Capabilities & BitmapCodecCapabilities.Encode) == 0)
            {
                var formatName = codecInfo?.FormatName ?? containerFormat.ToString();

                throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not support encoding {formatName} images.");
            }

            return new ImageSharpBitmapEncoderImpl(codecInfo, s_configuration, Allocator);
        }

        /// <summary>
        /// Converts stored resolution metadata to DPI, or null when the metadata carries
        /// no physical resolution (aspect-ratio-only or non-positive values).
        /// </summary>
        internal static Vector? TryGetDpi(ImageMetadata metadata)
        {
            var horizontal = metadata.HorizontalResolution;
            var vertical = metadata.VerticalResolution;

            if (horizontal <= 0 || vertical <= 0)
                return null;

            return metadata.ResolutionUnits switch
            {
                PixelResolutionUnit.PixelsPerInch => new Vector(horizontal, vertical),
                PixelResolutionUnit.PixelsPerCentimeter => new Vector(horizontal * 2.54, vertical * 2.54),
                PixelResolutionUnit.PixelsPerMeter => new Vector(horizontal * 0.0254, vertical * 0.0254),
                _ => null,
            };
        }

        private static bool TryIdentifyCore(Stream stream, out BitmapImageInfo info)
        {
            ImageInfo imageInfo;

            try
            {
                imageInfo = ISImage.Identify(new DecoderOptions { Configuration = s_configuration }, stream);
            }
            catch (ImageFormatException)
            {
                // Unknown magic, or headers a non-seekable prefix cannot satisfy.
                info = default;
                return false;
            }

            var codecInfo = ImageSharpCodecCatalog.FromImageFormat(imageInfo.Metadata.DecodedImageFormat);

            if (codecInfo is null)
            {
                info = default;
                return false;
            }

            info = new BitmapImageInfo(
                codecInfo.FormatName,
                new PixelSize(imageInfo.Width, imageInfo.Height),
                TryGetDpi(imageInfo.Metadata),
                TryMapNativeFormat(imageInfo.PixelType),
                GetFrameCount(imageInfo, codecInfo),
                imageInfo.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None);

            return true;
        }

        private static int? GetFrameCount(ImageInfo imageInfo, ImageSharpBitmapCodecInfo codecInfo)
        {
            if (imageInfo.FrameCount > 0)
                return imageInfo.FrameCount;

            // Identification did not count frames: unknown for multi-frame containers,
            // one for the inherently single-frame rest.
            return (codecInfo.Capabilities & BitmapCodecCapabilities.MultiFrame) != 0 ? null : 1;
        }

        private static PixelFormat? TryMapNativeFormat(PixelTypeInfo pixelType)
        {
            var color = pixelType.ColorType;
            var bits = pixelType.BitsPerPixel;
            var hasAlpha = pixelType.AlphaRepresentation != PixelAlphaRepresentation.None;

            if ((color & PixelColorType.Indexed) != 0)
                return null;

            // BGR carries the RGB channel bits plus an ordering marker, so it must be
            // tested before RGB.
            if ((color & PixelColorType.BGR) == PixelColorType.BGR)
            {
                return (bits, hasAlpha) switch
                {
                    (24, false) => PixelFormats.Bgr24,
                    (32, true) => PixelFormats.Bgra8888,
                    _ => null,
                };
            }

            if ((color & PixelColorType.RGB) == PixelColorType.RGB)
            {
                return (bits, hasAlpha) switch
                {
                    (24, false) => PixelFormats.Rgb24,
                    (32, true) => PixelFormats.Rgba8888,
                    (64, true) => PixelFormats.Rgba64,
                    _ => null,
                };
            }

            // JPEG and lossy WebP headers declare YCbCr; they decode to 8-bit RGB rows.
            if ((color & PixelColorType.YCbCr) == PixelColorType.YCbCr)
                return bits == 24 ? PixelFormats.Rgb24 : null;

            if ((color & PixelColorType.Luminance) == PixelColorType.Luminance)
            {
                return bits switch
                {
                    8 => PixelFormats.Gray8,
                    16 => PixelFormats.Gray16,
                    _ => null,
                };
            }

            return null;
        }

        private static PixelSize? ResolveFusedTarget(ImageSharpBitmapCodecInfo codecInfo,
            BitmapDecodeOptions? options, PixelSize nativeSize)
        {
            if ((codecInfo.Capabilities & BitmapCodecCapabilities.FusedDecode) == 0 ||
                options?.TargetSize is null || options.SourceRegion is not null)
            {
                return null;
            }

            var target = ImageSharpBitmapFrameSource.ResolveTargetSize(options.TargetSize, nativeSize);

            // Decode reduced only when the target shrinks the frame; the pipeline covers
            // everything else from the native decode, it should not enlarge a reduced one.
            if (target == nativeSize ||
                target.Width > nativeSize.Width || target.Height > nativeSize.Height)
            {
                return null;
            }

            return target;
        }

        private static MemoryStream ReadToMemory(Stream stream)
        {
            if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
            {
                var offset = segment.Offset + (int)memory.Position;
                var count = (int)(memory.Length - memory.Position);

                memory.Position = memory.Length;

                return new MemoryStream(segment.Array!, offset, count, writable: false);
            }

            var buffered = new MemoryStream();

            stream.CopyTo(buffered);
            buffered.Position = 0;

            return buffered;
        }

        private static Configuration CreateConfiguration() => new(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new GifConfigurationModule(),
            new BmpConfigurationModule(),
            new TiffConfigurationModule(),
            new WebpConfigurationModule())
        {
            // A decoded frame must expose one contiguous buffer so its pixels transfer
            // to the destination rental in single-copy row passes.
            PreferContiguousImageBuffers = true,
        };
    }
}
