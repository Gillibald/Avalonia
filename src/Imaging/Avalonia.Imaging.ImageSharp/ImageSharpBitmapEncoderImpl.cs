using System;
using System.IO;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;
using ISIccProfile = SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile;
using ISPngInterlaceMode = SixLabors.ImageSharp.Formats.Png.PngInterlaceMode;
using ISTiffCompression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression;
using PngInterlaceMode = Avalonia.Media.Imaging.PngInterlaceMode;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// Encodes through the ImageSharp encoders, reading the typed options off the
    /// concrete encoder classes. Multi-page TIFF realizes multi-frame encoding; the
    /// encode transform is applied natively via Mutate.
    /// </summary>
    internal sealed class ImageSharpBitmapEncoderImpl : IBitmapEncoderImpl
    {
        private readonly ImageSharpBitmapCodecInfo _codecInfo;
        private readonly Configuration _configuration;
        private readonly IBitmapMemoryAllocator _allocator;

        public ImageSharpBitmapEncoderImpl(ImageSharpBitmapCodecInfo codecInfo,
            Configuration configuration, IBitmapMemoryAllocator allocator)
        {
            _codecInfo = codecInfo;
            _configuration = configuration;
            _allocator = allocator;
        }

        public void Encode(BitmapEncoder encoder, Stream stream, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frames = encoder.Frames;
            var isMultiFrame = (_codecInfo.Capabilities & BitmapCodecCapabilities.MultiFrameEncode) != 0;

            using var image = CreateImage(frames[0].Pixels);

            ApplyFrameMetadata(image.Frames.RootFrame.Metadata, frames[0].Metadata);

            if (isMultiFrame)
            {
                for (var i = 1; i < frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var page = CreateImage(frames[i].Pixels);

                    var added = image.Frames.AddFrame(page.Frames.RootFrame);

                    ApplyFrameMetadata(added.Metadata, frames[i].Metadata);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            ApplyContainerMetadata(encoder, image);
            ApplyTransform(encoder.Transform, image);

            image.Save(stream, CreateEncoder(encoder));
        }

        private unsafe Image<Bgra32> CreateImage(PixelBuffer pixels)
        {
            using var source = pixels.Lock();

            var size = source.Size;
            var rowBytes = size.Width * 4;
            var byteCount = checked(rowBytes * size.Height);
            Image<Bgra32> image;

            // ImageSharp's Bgra32 is straight alpha, so premultiplied input transcodes
            // to unassociated values on the way in.
            if (source.Format == PixelFormats.Bgra8888 &&
                source.AlphaFormat != AlphaFormat.Premul &&
                source.RowBytes == rowBytes)
            {
                image = ISImage.LoadPixelData<Bgra32>(_configuration,
                    new ReadOnlySpan<byte>((void*)source.Address, byteCount), size.Width, size.Height);
            }
            else
            {
                var staging = _allocator.Rent(byteCount);

                try
                {
                    var plan = new FusedPlanExecution(new PixelRect(size), size,
                        PixelFormats.Bgra8888,
                        source.AlphaFormat == AlphaFormat.Premul ? AlphaFormat.Unpremul : source.AlphaFormat,
                        BitmapInterpolationMode.HighQuality);

                    FusedPixelPipeline.Run(source, plan, staging.Address, rowBytes, _allocator);

                    image = ISImage.LoadPixelData<Bgra32>(_configuration,
                        new ReadOnlySpan<byte>((void*)staging.Address, byteCount), size.Width, size.Height);
                }
                finally
                {
                    staging.Dispose();
                }
            }

            var dpi = pixels.Dpi;

            if (dpi.X > 0 && dpi.Y > 0)
            {
                image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
                image.Metadata.HorizontalResolution = dpi.X;
                image.Metadata.VerticalResolution = dpi.Y;
            }

            return image;
        }

        private void ApplyContainerMetadata(BitmapEncoder encoder, Image<Bgra32> image)
        {
            if (encoder.Metadata is { } metadata)
            {
                switch (metadata)
                {
                    case ImageSharpExifMetadata exif:
                        // TIFF readers (including this backend) find the tags on the
                        // page, other containers on the image; install on both.
                        InstallExifMetadata(exif, image.Metadata, image.Frames.RootFrame.Metadata);
                        break;
                    case ImageSharpPngTextMetadata text:
                        InstallPngTextMetadata(text, image.Metadata);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"The 'ImageSharp' imaging backend cannot encode metadata of type {metadata.GetType().Name}.");
                }
            }

            if (encoder.ColorProfile is { } colorProfile)
                image.Metadata.IccProfile = new ISIccProfile(colorProfile.ToArray());
        }

        private static void ApplyFrameMetadata(ImageFrameMetadata target, BitmapMetadata? metadata)
        {
            switch (metadata)
            {
                case null:
                    break;
                case ImageSharpExifMetadata exif:
                    InstallExifMetadata(exif, null, target);
                    break;
                default:
                    throw new NotSupportedException(
                        $"The 'ImageSharp' imaging backend cannot encode frame metadata of type {metadata.GetType().Name}.");
            }
        }

        private static void InstallExifMetadata(ImageSharpExifMetadata metadata,
            ImageMetadata? imageMetadata, ImageFrameMetadata? frameMetadata)
        {
            var exif = metadata.ExifProfile.Values.Count > 0 ? metadata.ExifProfile : null;
            var xmp = metadata.XmpProfile;
            var icc = metadata.IccProfileData;

            if (imageMetadata is not null)
            {
                imageMetadata.ExifProfile = exif?.DeepClone() ?? imageMetadata.ExifProfile;
                imageMetadata.XmpProfile = xmp?.DeepClone() ?? imageMetadata.XmpProfile;
                imageMetadata.IccProfile = icc?.DeepClone() ?? imageMetadata.IccProfile;
            }

            if (frameMetadata is not null)
            {
                frameMetadata.ExifProfile = exif?.DeepClone() ?? frameMetadata.ExifProfile;
                frameMetadata.XmpProfile = xmp?.DeepClone() ?? frameMetadata.XmpProfile;
                frameMetadata.IccProfile = icc?.DeepClone() ?? frameMetadata.IccProfile;
            }
        }

        private static void InstallPngTextMetadata(ImageSharpPngTextMetadata metadata, ImageMetadata target)
        {
            var pngMetadata = target.GetPngMetadata();

            foreach (var entry in metadata.Entries)
                pngMetadata.TextData.Add(entry);
        }

        private static void ApplyTransform(BitmapTransform transform, ISImage image)
        {
            if (transform.IsIdentity)
                return;

            image.Mutate(context =>
            {
                if (transform.Rotation != BitmapRotation.None)
                    context.Rotate(ToRotateMode(transform.Rotation));

                if (transform.FlipHorizontal)
                    context.Flip(FlipMode.Horizontal);

                if (transform.FlipVertical)
                    context.Flip(FlipMode.Vertical);
            });
        }

        private IImageEncoder CreateEncoder(BitmapEncoder encoder) => encoder switch
        {
            PngBitmapEncoder png => new PngEncoder
            {
                CompressionLevel = (PngCompressionLevel)Math.Clamp(png.CompressionLevel, 0, 9),
                FilterMethod = ToPngFilterMethod(png.Filter),
                InterlaceMethod = png.Interlace == PngInterlaceMode.Adam7
                    ? ISPngInterlaceMode.Adam7
                    : ISPngInterlaceMode.None,
            },
            JpegBitmapEncoder jpeg => new JpegEncoder { Quality = jpeg.Quality },
            WebpBitmapEncoder webp => new WebpEncoder
            {
                Quality = webp.Quality,
                FileFormat = webp.Lossless ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy,
            },
            TiffBitmapEncoder tiff => new TiffEncoder { Compression = ToTiffCompression(tiff.Compression) },
            GifBitmapEncoder => new GifEncoder(),
            BmpBitmapEncoder => new BmpEncoder(),
            _ => CreateDefaultEncoder(),
        };

        private IImageEncoder CreateDefaultEncoder()
        {
            var containerFormat = _codecInfo.ContainerFormat;

            if (containerFormat == BitmapContainerFormats.Png)
                return new PngEncoder();
            if (containerFormat == BitmapContainerFormats.Jpeg)
                return new JpegEncoder();
            if (containerFormat == BitmapContainerFormats.Gif)
                return new GifEncoder();
            if (containerFormat == BitmapContainerFormats.Bmp)
                return new BmpEncoder();
            if (containerFormat == BitmapContainerFormats.Tiff)
                return new TiffEncoder();
            if (containerFormat == BitmapContainerFormats.Webp)
                return new WebpEncoder();

            throw new NotSupportedException(
                $"The 'ImageSharp' imaging backend does not support encoding {_codecInfo.FormatName} images.");
        }

        private static RotateMode ToRotateMode(BitmapRotation rotation) => rotation switch
        {
            BitmapRotation.Rotate90 => RotateMode.Rotate90,
            BitmapRotation.Rotate180 => RotateMode.Rotate180,
            BitmapRotation.Rotate270 => RotateMode.Rotate270,
            _ => RotateMode.None,
        };

        private static PngFilterMethod ToPngFilterMethod(PngFilterMode mode) => mode switch
        {
            PngFilterMode.Auto => PngFilterMethod.Adaptive,
            PngFilterMode.None => PngFilterMethod.None,
            PngFilterMode.Sub => PngFilterMethod.Sub,
            PngFilterMode.Up => PngFilterMethod.Up,
            PngFilterMode.Average => PngFilterMethod.Average,
            PngFilterMode.Paeth => PngFilterMethod.Paeth,
            _ => PngFilterMethod.Adaptive,
        };

        private static ISTiffCompression ToTiffCompression(TiffCompression compression) => compression switch
        {
            TiffCompression.None => ISTiffCompression.None,
            TiffCompression.Lzw => ISTiffCompression.Lzw,
            TiffCompression.Deflate => ISTiffCompression.Deflate,
            TiffCompression.PackBits => ISTiffCompression.PackBits,
            TiffCompression.Ccitt3 => ISTiffCompression.CcittGroup3Fax,
            TiffCompression.Ccitt4 => ISTiffCompression.CcittGroup4Fax,
            _ => ISTiffCompression.Lzw,
        };
    }
}
