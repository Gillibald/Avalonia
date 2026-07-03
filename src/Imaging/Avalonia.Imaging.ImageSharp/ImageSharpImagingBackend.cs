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
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
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
        // Complete payloads always identify; a prefix identifies as long as the
        // container's chunk chain fits, because ImageSharp walks it for frame counts
        // and trailing metadata.
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
                // Identification walks the whole chunk chain (frame counts, trailing
                // metadata), so the stream is handed over directly rather than as a
                // prefix; only headers are read and no pixels decode.
                return TryIdentifyCore(stream, out info);
            }
            finally
            {
                stream.Position = position;
            }
        }

        /// <inheritdoc />
        public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info)
        {
            ImageInfo imageInfo;

            try
            {
                imageInfo = ISImage.Identify(new DecoderOptions { Configuration = s_configuration }, data);
            }
            catch (ImageFormatException)
            {
                // Unknown magic, or headers the prefix cannot satisfy.
                info = default;
                return false;
            }

            var described = Describe(imageInfo);

            info = described ?? default;

            return described is not null;
        }

        /// <inheritdoc />
        public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
        {
            _ = stream ?? throw new ArgumentNullException(nameof(stream));

            // ImageSharp decodes eagerly, so the encoded data is materialized up front
            // regardless of MaterializeSource and the caller's stream is entirely free
            // once this method returns. Buffering also covers partial reads.
            using var buffered = ReadToMemory(stream);

            options?.CancellationToken.ThrowIfCancellationRequested();

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

            // The header facts are taken before the decode plan is applied, so the
            // decoder's Info reports pre-plan source values (raw stored dimensions).
            var headerInfo = Describe(imageInfo)!.Value;

            var orientation = options?.RespectExifOrientation ?? true
                ? ReadOrientation(imageInfo)
                : PixelOrientation.Normal;

            // The decode plan is expressed in oriented display space.
            var orientedSize = FusedPixelPipeline.GetOrientedSize(headerInfo.PixelSize, orientation);
            var fusedTarget = ResolveFusedTarget(codecInfo, options, orientedSize);

            // The loader resizes the raw image before any orientation applies, so a
            // transposing orientation swaps the requested axes.
            var loaderTarget = fusedTarget is { } target
                ? FusedPixelPipeline.GetOrientedSize(target, orientation)
                : (PixelSize?)null;

            var decoderOptions = new DecoderOptions
            {
                Configuration = s_configuration,
                MaxFrames = (codecInfo.Capabilities & BitmapCodecCapabilities.MultiFrame) != 0 ? uint.MaxValue : 1,
                TargetSize = loaderTarget is { } raw ? new ISSize(raw.Width, raw.Height) : null,
            };

            buffered.Position = 0;

            ISImage image;

            // ImageSharp decodes eagerly here, so the concurrency gate covers the load
            // and the fused orientation pass.
            using (DecodeConcurrencyLimiter.Enter(options?.CancellationToken ?? default))
            {
                image = ISImage.Load(decoderOptions, buffered);

                if (orientation != PixelOrientation.Normal)
                {
                    try
                    {
                        // The loaded image becomes display-oriented; AutoOrient also
                        // resets the stored orientation tag.
                        image.Mutate(context => context.AutoOrient());
                    }
                    catch
                    {
                        image.Dispose();
                        throw;
                    }
                }
            }

            try
            {
                var usedDecodeScale = fusedTarget is not null &&
                    (image.Width != orientedSize.Width || image.Height != orientedSize.Height);

                return new ImageSharpBitmapDecoder(image, codecInfo, headerInfo, options, Allocator,
                    orientation, usedDecodeScale);
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
                info = default;
                return false;
            }

            var described = Describe(imageInfo);

            info = described ?? default;

            return described is not null;
        }

        internal static BitmapImageInfo? Describe(ImageInfo imageInfo)
        {
            var codecInfo = ImageSharpCodecCatalog.FromImageFormat(imageInfo.Metadata.DecodedImageFormat);

            if (codecInfo is null)
                return null;

            return new BitmapImageInfo(
                codecInfo.FormatName,
                new PixelSize(imageInfo.Width, imageInfo.Height),
                TryGetDpi(imageInfo.Metadata),
                TryMapNativeFormat(imageInfo.PixelType),
                GetFrameCount(imageInfo, codecInfo),
                imageInfo.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None);
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

        private static PixelOrientation ReadOrientation(ImageInfo imageInfo)
        {
            var exif = imageInfo.Metadata.ExifProfile;

            if (exif is null && imageInfo.FrameMetadataCollection is { Count: > 0 } frames)
                exif = frames[0].ExifProfile;

            if (exif is not null && exif.TryGetValue(ExifTag.Orientation, out var value) &&
                value.Value is >= 1 and <= 8)
            {
                return (PixelOrientation)value.Value;
            }

            return PixelOrientation.Normal;
        }

        // Both the requested target and the result are in oriented display space.
        private static PixelSize? ResolveFusedTarget(ImageSharpBitmapCodecInfo codecInfo,
            BitmapDecodeOptions? options, PixelSize orientedSize)
        {
            if ((codecInfo.Capabilities & BitmapCodecCapabilities.FusedDecode) == 0 ||
                options?.TargetSize is null || options.SourceRegion is not null)
            {
                return null;
            }

            var target = ImageSharpBitmapFrameSource.ResolveTargetSize(options.TargetSize, orientedSize);

            // Decode reduced only when the target shrinks the frame; the pipeline covers
            // everything else from the native decode, it should not enlarge a reduced one.
            if (target == orientedSize ||
                target.Width > orientedSize.Width || target.Height > orientedSize.Height)
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
