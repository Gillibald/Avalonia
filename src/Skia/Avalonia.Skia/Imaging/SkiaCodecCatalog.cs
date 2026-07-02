using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    internal sealed class SkiaBitmapCodecInfo : IBitmapCodecInfo
    {
        public SkiaBitmapCodecInfo(string formatName, SKEncodedImageFormat encodedFormat,
            Guid containerFormat, BitmapCodecCapabilities capabilities,
            string[] mimeTypes, string[] fileExtensions)
        {
            FormatName = formatName;
            EncodedFormat = encodedFormat;
            ContainerFormat = containerFormat;
            Capabilities = capabilities;
            MimeTypes = mimeTypes;
            FileExtensions = fileExtensions;
        }

        public string FormatName { get; }

        public SKEncodedImageFormat EncodedFormat { get; }

        public IReadOnlyList<string> MimeTypes { get; }

        public IReadOnlyList<string> FileExtensions { get; }

        public Guid ContainerFormat { get; }

        public BitmapCodecCapabilities Capabilities { get; }
    }

    /// <summary>
    /// The per-format capability catalog of the SkiaSharp imaging backend.
    /// </summary>
    internal static class SkiaCodecCatalog
    {
        // Formats without a WIC container identifier get stable private ones.
        private static readonly Guid s_wbmpContainer = new("7defb352-6c22-4bb0-8f56-7be6a5f6ea54");
        private static readonly Guid s_dngContainer = new("13e0fbd4-fdd6-4f87-b1d5-c0f2f5f9c3a2");
        private static readonly Guid s_icoContainer = new("a3a860c4-338f-4c17-919a-fba4b5628f21");

        public static IReadOnlyList<SkiaBitmapCodecInfo> All { get; } = new[]
        {
            new SkiaBitmapCodecInfo("PNG", SKEncodedImageFormat.Png, BitmapContainerFormats.Png,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.Encode,
                new[] { "image/png" }, new[] { "png" }),
            new SkiaBitmapCodecInfo("JPEG", SKEncodedImageFormat.Jpeg, BitmapContainerFormats.Jpeg,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.FusedDecode | BitmapCodecCapabilities.Encode,
                new[] { "image/jpeg" }, new[] { "jpg", "jpeg" }),
            new SkiaBitmapCodecInfo("WebP", SKEncodedImageFormat.Webp, BitmapContainerFormats.Webp,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.FusedDecode | BitmapCodecCapabilities.Encode,
                new[] { "image/webp" }, new[] { "webp" }),
            new SkiaBitmapCodecInfo("GIF", SKEncodedImageFormat.Gif, BitmapContainerFormats.Gif,
                BitmapCodecCapabilities.Decode,
                new[] { "image/gif" }, new[] { "gif" }),
            new SkiaBitmapCodecInfo("BMP", SKEncodedImageFormat.Bmp, BitmapContainerFormats.Bmp,
                BitmapCodecCapabilities.Decode,
                new[] { "image/bmp" }, new[] { "bmp", "dib" }),
            new SkiaBitmapCodecInfo("ICO", SKEncodedImageFormat.Ico, s_icoContainer,
                BitmapCodecCapabilities.Decode,
                new[] { "image/x-icon", "image/vnd.microsoft.icon" }, new[] { "ico" }),
            new SkiaBitmapCodecInfo("WBMP", SKEncodedImageFormat.Wbmp, s_wbmpContainer,
                BitmapCodecCapabilities.Decode,
                new[] { "image/vnd.wap.wbmp" }, new[] { "wbmp" }),
            new SkiaBitmapCodecInfo("DNG", SKEncodedImageFormat.Dng, s_dngContainer,
                BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.FusedDecode,
                new[] { "image/x-adobe-dng" }, new[] { "dng" }),
        };

        public static SkiaBitmapCodecInfo? FromEncodedFormat(SKEncodedImageFormat format)
        {
            foreach (var codec in All)
            {
                if (codec.EncodedFormat == format)
                    return codec;
            }

            return null;
        }

        public static SkiaBitmapCodecInfo? FromContainerFormat(Guid containerFormat)
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
