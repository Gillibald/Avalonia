using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    /// <summary>
    /// Encodes through the Skia encoders (PNG, JPEG, WebP), reading the typed options
    /// off the concrete encoder classes.
    /// </summary>
    internal sealed class SkiaBitmapEncoderImpl : IBitmapEncoderImpl
    {
        private readonly SKEncodedImageFormat _format;
        private readonly IBitmapMemoryAllocator _allocator;

        public SkiaBitmapEncoderImpl(SKEncodedImageFormat format, IBitmapMemoryAllocator allocator)
        {
            _format = format;
            _allocator = allocator;
        }

        public void Encode(BitmapEncoder encoder, Stream stream)
        {
            if (!encoder.Transform.IsIdentity)
            {
                throw new NotSupportedException(
                    "The 'SkiaSharp' imaging backend does not support transforming while encoding.");
            }

            if (encoder is PngBitmapEncoder { Interlace: PngInterlaceMode.Adam7 })
            {
                throw new NotSupportedException(
                    "The 'SkiaSharp' imaging backend does not support Adam7 interlacing.");
            }

            var pixels = encoder.Frames[0].Pixels;

            using var framebuffer = pixels.Lock();

            if (CanEncodeDirectly(framebuffer.Format))
            {
                EncodeFramebuffer(encoder, framebuffer, stream);
            }
            else
            {
                // Convert to a Skia-native format in one pipeline pass, then encode.
                var size = framebuffer.Size;
                var rowBytes = size.Width * 4;
                var staging = _allocator.Rent((long)rowBytes * size.Height);

                try
                {
                    var plan = new FusedPlanExecution(new PixelRect(size), size,
                        PixelFormats.Bgra8888, framebuffer.AlphaFormat,
                        BitmapInterpolationMode.HighQuality);

                    FusedPixelPipeline.Run(framebuffer, plan, staging.Address, rowBytes);

                    var converted = new LockedFramebuffer(staging.Address, size, rowBytes,
                        framebuffer.Dpi, PixelFormats.Bgra8888, framebuffer.AlphaFormat, null);

                    EncodeFramebuffer(encoder, converted, stream);
                }
                finally
                {
                    staging.Dispose();
                }
            }
        }

        private void EncodeFramebuffer(BitmapEncoder encoder, ILockedFramebuffer framebuffer, Stream stream)
        {
            var info = new SKImageInfo(framebuffer.Size.Width, framebuffer.Size.Height,
                framebuffer.Format.ToSkColorType(), framebuffer.AlphaFormat.ToSkAlphaType());

            using var pixmap = new SKPixmap(info, framebuffer.Address, framebuffer.RowBytes);

            var success = encoder switch
            {
                PngBitmapEncoder png => pixmap.Encode(stream,
                    new SKPngEncoderOptions(ToFilterFlags(png.Filter), png.CompressionLevel)),
                JpegBitmapEncoder jpeg => pixmap.Encode(stream,
                    new SKJpegEncoderOptions(jpeg.Quality)),
                WebpBitmapEncoder webp => pixmap.Encode(stream,
                    new SKWebpEncoderOptions(
                        webp.Lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy,
                        webp.Quality)),
                _ => pixmap.Encode(stream, _format, 100),
            };

            if (!success)
                throw new IOException($"Encoding to {_format} failed.");
        }

        private static bool CanEncodeDirectly(PixelFormat format) =>
            format == PixelFormats.Bgra8888 ||
            format == PixelFormats.Rgba8888 ||
            format == PixelFormats.Rgb565 ||
            format == PixelFormats.Rgb32;

        private static SKPngEncoderFilterFlags ToFilterFlags(PngFilterMode mode) => mode switch
        {
            PngFilterMode.Auto => SKPngEncoderFilterFlags.AllFilters,
            PngFilterMode.None => SKPngEncoderFilterFlags.NoFilters,
            PngFilterMode.Sub => SKPngEncoderFilterFlags.Sub,
            PngFilterMode.Up => SKPngEncoderFilterFlags.Up,
            PngFilterMode.Average => SKPngEncoderFilterFlags.Avg,
            PngFilterMode.Paeth => SKPngEncoderFilterFlags.Paeth,
            _ => SKPngEncoderFilterFlags.AllFilters,
        };
    }
}
