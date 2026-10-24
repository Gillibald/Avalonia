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
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using ISColor = SixLabors.ImageSharp.Color;
using ISImage = SixLabors.ImageSharp.Image;
using ISPaletteQuantizer = SixLabors.ImageSharp.Processing.Processors.Quantization.PaletteQuantizer;
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
            var screen = (encoder as GifBitmapEncoder)?.GetScreenSize();
            var palettes = new BitmapPalette?[isMultiFrame ? frames.Count : 1];

            using var image = CreateImage(encoder, 0, screen, out palettes[0]);

            ApplyFrameMetadata(image.Frames.RootFrame.Metadata, frames[0].Metadata);

            if (isMultiFrame)
            {
                for (var i = 1; i < frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var page = CreateImage(encoder, i, screen, out palettes[i]);

                    var added = image.Frames.AddFrame(page.Frames.RootFrame);

                    ApplyFrameMetadata(added.Metadata, frames[i].Metadata);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            ApplyContainerMetadata(encoder, image);
            ApplyColorProfiles(encoder, image);

            if (encoder is GifBitmapEncoder gif)
                ApplyGifAnimation(gif, image, palettes);

            ApplyTransform(encoder.Transform, image);

            image.Save(stream, CreateEncoder(encoder, palettes));
        }

        // Every frame of an ImageSharp image has the image size, so a GIF frame is placed on
        // a transparent canvas the size of the logical screen; the GIF encoder trims each
        // frame back to the region it changes.
        private Image<Bgra32> CreateImage(BitmapEncoder encoder, int index, PixelSize? screen,
            out BitmapPalette? palette)
        {
            var image = CreateImage(encoder, index, out palette);
            var origin = encoder.Frames[index].Origin;

            if (screen is not { } size || (size.Width == image.Width && size.Height == image.Height))
                return image;

            using (image)
            {
                var canvas = new Image<Bgra32>(_configuration, size.Width, size.Height);

                canvas.Metadata.ResolutionUnits = image.Metadata.ResolutionUnits;
                canvas.Metadata.HorizontalResolution = image.Metadata.HorizontalResolution;
                canvas.Metadata.VerticalResolution = image.Metadata.VerticalResolution;

                canvas.ProcessPixelRows(image, (target, source) =>
                {
                    for (var y = 0; y < source.Height; y++)
                        source.GetRowSpan(y).CopyTo(target.GetRowSpan(origin.Y + y).Slice(origin.X));
                });

                return canvas;
            }
        }

        private unsafe Image<Bgra32> CreateImage(BitmapEncoder encoder, int index, out BitmapPalette? palette)
        {
            var view = encoder.LockFramePixels(index, out palette);

            // ImageSharp quantizes from colors, so indexed pixels are expanded here and the
            // palette is handed to the format encoder.
            if (palette is not null)
            {
                using var indexed = view;

                view =PixelBuffer.CopyFrom(view, palette).Convert(PixelFormats.Bgra8888, AlphaFormat.Unpremul).Lock();
            }

            using var source = view;

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

            var dpi = source.Dpi;

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
                var store = ToImageSharpStore(metadata);

                // TIFF readers (including this backend) find the tags on the page, other
                // containers on the image; install on both.
                InstallProfiles(store, image.Metadata, image.Frames.RootFrame.Metadata);

                if (store.Text is { Count: > 0 } text)
                {
                    var pngMetadata = image.Metadata.GetPngMetadata();

                    foreach (var entry in text)
                        pngMetadata.TextData.Add(entry);
                }
            }
        }

        // PNG, JPEG and WebP write the image-level profile and TIFF writes each page's, so
        // the first frame's profile goes on both.
        private static void ApplyColorProfiles(BitmapEncoder encoder, Image<Bgra32> image)
        {
            var frameCount = Math.Min(encoder.Frames.Count, image.Frames.Count);

            for (var i = 0; i < frameCount; i++)
            {
                if ((encoder.Frames[i].ColorProfile ?? encoder.ColorProfile) is not { } profile)
                    continue;

                image.Frames[i].Metadata.IccProfile = new ISIccProfile(profile.ToArray());

                if (i == 0)
                    image.Metadata.IccProfile = new ISIccProfile(profile.ToArray());
            }
        }

        private static void ApplyFrameMetadata(ImageFrameMetadata target, BitmapMetadata? metadata)
        {
            if (metadata is not null)
                InstallProfiles(ToImageSharpStore(metadata), null, target);
        }

        // Metadata decoded by this backend carries its profiles already; any other store,
        // such as metadata authored from scratch, is copied value by value, which fails for
        // paths ImageSharp cannot hold.
        private static ImageSharpMetadataStore ToImageSharpStore(BitmapMetadata metadata)
        {
            if (metadata.Store is ImageSharpMetadataStore own)
                return own;

            var store = ImageSharpMetadataStore.CreateEmpty(metadata.ContainerFormat);

            foreach (var path in metadata.Store.EnumeratePaths())
            {
                if (metadata.Store.TryRead(path, out var value))
                    store.Write(path, value);
            }

            return store;
        }

        private static void InstallProfiles(ImageSharpMetadataStore store, ImageMetadata? imageMetadata,
            ImageFrameMetadata? frameMetadata)
        {
            if (imageMetadata is not null)
            {
                imageMetadata.ExifProfile = store.Exif?.DeepClone() ?? imageMetadata.ExifProfile;
                imageMetadata.XmpProfile = store.Xmp?.DeepClone() ?? imageMetadata.XmpProfile;
                imageMetadata.IccProfile = store.Icc?.DeepClone() ?? imageMetadata.IccProfile;
                imageMetadata.IptcProfile = store.Iptc?.DeepClone() ?? imageMetadata.IptcProfile;
            }

            if (frameMetadata is not null)
            {
                frameMetadata.ExifProfile = store.Exif?.DeepClone() ?? frameMetadata.ExifProfile;
                frameMetadata.XmpProfile = store.Xmp?.DeepClone() ?? frameMetadata.XmpProfile;
                frameMetadata.IccProfile = store.Icc?.DeepClone() ?? frameMetadata.IccProfile;
                frameMetadata.IptcProfile = store.Iptc?.DeepClone() ?? frameMetadata.IptcProfile;
            }
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

        private static void ApplyGifAnimation(GifBitmapEncoder encoder, Image<Bgra32> image, BitmapPalette?[] palettes)
        {
            var gifMetadata = image.Metadata.GetGifMetadata();

            gifMetadata.RepeatCount = (ushort)encoder.LoopCount;

            if (encoder.GlobalPalette is { } global)
            {
                gifMetadata.ColorTableMode = FrameColorTableMode.Global;
                gifMetadata.GlobalColorTable = ToImageSharpColors(global);
            }

            for (var i = 0; i < image.Frames.Count; i++)
            {
                var frame = encoder.Frames[i];
                var frameMetadata = image.Frames[i].Metadata.GetGifMetadata();

                frameMetadata.FrameDelay = (int)Math.Round(frame.Delay.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
                frameMetadata.DisposalMode = ToDisposalMode(frame.Disposal);

                // Frames written with the global palette keep the global table; any other
                // palette, set on the frame or taken from an indexed source, becomes a local table.
                if (palettes[i] is { } local && !ReferenceEquals(local, encoder.GlobalPalette))
                {
                    frameMetadata.ColorTableMode = FrameColorTableMode.Local;
                    frameMetadata.LocalColorTable = ToImageSharpColors(local);
                }
                else if (encoder.GlobalPalette is not null)
                {
                    frameMetadata.ColorTableMode = FrameColorTableMode.Global;
                }
            }
        }

        private static FrameDisposalMode ToDisposalMode(FrameDisposalMethod disposal) => disposal switch
        {
            FrameDisposalMethod.DoNotDispose => FrameDisposalMode.DoNotDispose,
            FrameDisposalMethod.RestoreToBackground => FrameDisposalMode.RestoreToBackground,
            FrameDisposalMethod.RestoreToPrevious => FrameDisposalMode.RestoreToPrevious,
            _ => FrameDisposalMode.Unspecified,
        };

        private static ReadOnlyMemory<ISColor> ToImageSharpColors(BitmapPalette palette)
        {
            var colors = new ISColor[palette.Colors.Count];

            for (var i = 0; i < colors.Length; i++)
            {
                var color = palette.Colors[i];

                colors[i] = ISColor.FromPixel(new Rgba32(color.R, color.G, color.B, color.A));
            }

            return colors;
        }

        // The pixels already hold palette colors, so exact matching without dithering
        // reproduces every pixel.
        private static ISPaletteQuantizer? CreatePaletteQuantizer(BitmapPalette?[] palettes, BitmapPalette? preferred)
        {
            var palette = preferred ?? Array.Find(palettes, p => p is not null);

            return palette is null ? null : new ISPaletteQuantizer(ToImageSharpColors(palette), new QuantizerOptions
            {
                Dither = null,
                ColorMatchingMode = ColorMatchingMode.Exact,
                MaxColors = palette.Colors.Count,
            });
        }

        private IImageEncoder CreateEncoder(BitmapEncoder encoder, BitmapPalette?[] palettes) => encoder switch
        {
            PngBitmapEncoder png when palettes[0] is { } palette => new PngEncoder
            {
                CompressionLevel = (PngCompressionLevel)Math.Clamp(png.CompressionLevel, 0, 9),
                FilterMethod = ToPngFilterMethod(png.Filter),
                InterlaceMethod = png.Interlace == PngInterlaceMode.Adam7
                    ? ISPngInterlaceMode.Adam7
                    : ISPngInterlaceMode.None,
                ColorType = PngColorType.Palette,
                Quantizer = CreatePaletteQuantizer(palettes, palette),
            },
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
            GifBitmapEncoder gif when CreatePaletteQuantizer(palettes, gif.GlobalPalette) is { } quantizer => new GifEncoder
            {
                Quantizer = quantizer,
                ColorTableMode = gif.GlobalPalette is not null ? FrameColorTableMode.Global : FrameColorTableMode.Local,
            },
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
