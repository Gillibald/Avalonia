using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp.Formats;

namespace Avalonia.Imaging.ImageSharp
{
    internal sealed class ImageSharpBitmapCodecInfo : IBitmapCodecInfo
    {
        public ImageSharpBitmapCodecInfo(string formatName, string imageSharpName,
            Guid containerFormat, BitmapCodecCapabilities capabilities,
            string[] mimeTypes, string[] fileExtensions)
        {
            FormatName = formatName;
            ImageSharpName = imageSharpName;
            ContainerFormat = containerFormat;
            Capabilities = capabilities;
            MimeTypes = mimeTypes;
            FileExtensions = fileExtensions;
        }

        public string FormatName { get; }

        /// <summary>
        /// Gets the format name ImageSharp reports through <see cref="IImageFormat.Name"/>.
        /// </summary>
        public string ImageSharpName { get; }

        public IReadOnlyList<string> MimeTypes { get; }

        public IReadOnlyList<string> FileExtensions { get; }

        public Guid ContainerFormat { get; }

        public BitmapCodecCapabilities Capabilities { get; }
    }

    /// <summary>
    /// The per-format capability catalog of the ImageSharp imaging backend.
    /// </summary>
    /// <remarks>
    /// Animation and palettes are deferred: ImageSharp composites animated frames during
    /// decode and exposes no raw index buffer. FusedDecode is declared for JPEG only,
    /// where <c>DecoderOptions.TargetSize</c> measurably reduces the decode itself; the
    /// other formats decode at native size and internally resize afterwards, so the
    /// shared pixel pipeline applies their scaling instead.
    /// </remarks>
    internal static class ImageSharpCodecCatalog
    {
        public static IReadOnlyList<ImageSharpBitmapCodecInfo> All { get; } = new[]
        {
            new ImageSharpBitmapCodecInfo("PNG", "PNG", BitmapContainerFormats.Png,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode |
                BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile |
                BitmapCodecCapabilities.HighDepth,
                new[] { "image/png" }, new[] { "png" }),
            new ImageSharpBitmapCodecInfo("JPEG", "JPEG", BitmapContainerFormats.Jpeg,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode |
                BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile |
                BitmapCodecCapabilities.FusedDecode,
                new[] { "image/jpeg" }, new[] { "jpg", "jpeg" }),
            new ImageSharpBitmapCodecInfo("GIF", "GIF", BitmapContainerFormats.Gif,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.MultiFrame |
                BitmapCodecCapabilities.Encode,
                new[] { "image/gif" }, new[] { "gif" }),
            new ImageSharpBitmapCodecInfo("BMP", "BMP", BitmapContainerFormats.Bmp,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode,
                new[] { "image/bmp" }, new[] { "bmp", "dib" }),
            new ImageSharpBitmapCodecInfo("TIFF", "TIFF", BitmapContainerFormats.Tiff,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode |
                BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile |
                BitmapCodecCapabilities.MultiFrame | BitmapCodecCapabilities.MultiFrameEncode |
                BitmapCodecCapabilities.HighDepth,
                new[] { "image/tiff" }, new[] { "tif", "tiff" }),
            new ImageSharpBitmapCodecInfo("WebP", "WEBP", BitmapContainerFormats.Webp,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode,
                new[] { "image/webp" }, new[] { "webp" }),
        };

        public static ImageSharpBitmapCodecInfo? FromImageFormat(IImageFormat? format)
        {
            if (format is null)
                return null;

            foreach (var codec in All)
            {
                if (string.Equals(codec.ImageSharpName, format.Name, StringComparison.OrdinalIgnoreCase))
                    return codec;
            }

            return null;
        }

        public static ImageSharpBitmapCodecInfo? FromContainerFormat(Guid containerFormat)
        {
            foreach (var codec in All)
            {
                if (codec.ContainerFormat == containerFormat)
                    return codec;
            }

            return null;
        }
    }
}
