using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    internal sealed class PixelManCodecInfo : IBitmapCodecInfo
    {
        public PixelManCodecInfo(string formatName, PmCodecKind kind, Guid containerFormat,
            BitmapCodecCapabilities capabilities, string[] mimeTypes, string[] fileExtensions)
        {
            FormatName = formatName;
            Kind = kind;
            ContainerFormat = containerFormat;
            Capabilities = capabilities;
            MimeTypes = mimeTypes;
            FileExtensions = fileExtensions;
        }

        public string FormatName { get; }

        public PmCodecKind Kind { get; }

        public IReadOnlyList<string> MimeTypes { get; }

        public IReadOnlyList<string> FileExtensions { get; }

        public Guid ContainerFormat { get; }

        public BitmapCodecCapabilities Capabilities { get; }
    }

    /// <summary>
    /// The per-format capability catalog of the PixelMan imaging backend. PixelMan decodes
    /// every format into its stored layout and encodes multi-page TIFF and animated GIF.
    /// WebP and ICO are not in the catalog: PixelMan has no codec for them, and the
    /// backend does not fall back to another one.
    /// </summary>
    internal static class PixelManCodecCatalog
    {
        private const BitmapCodecCapabilities Common = BitmapCodecCapabilities.Decode |
            BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.PreservesStoredFormat;

        // JPEG and PNG decode reduced towards a target size.
        private const BitmapCodecCapabilities Scaling = BitmapCodecCapabilities.FusedDecode;

        private static readonly IReadOnlyList<PixelManCodecInfo> s_all = Create();

        public static IReadOnlyList<PixelManCodecInfo> All => s_all;

        public static PixelManCodecInfo? FromKind(PmCodecKind kind)
        {
            foreach (var codec in s_all)
            {
                if (codec.Kind == kind)
                    return codec;
            }

            return null;
        }

        public static PixelManCodecInfo? FromContainerFormat(Guid containerFormat)
        {
            foreach (var codec in s_all)
            {
                if (codec.ContainerFormat == containerFormat)
                    return codec;
            }

            return null;
        }

        private static IReadOnlyList<PixelManCodecInfo> Create()
        {
            var codecs = new[]
            {
                new PixelManCodecInfo("PNG", PmCodecKind.Png, BitmapContainerFormats.Png,
                    Common | BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile |
                    BitmapCodecCapabilities.HighDepth | BitmapCodecCapabilities.Palette | Scaling,
                    new[] { "image/png" }, new[] { "png" }),
                new PixelManCodecInfo("JPEG", PmCodecKind.Jpeg, BitmapContainerFormats.Jpeg,
                    Common | BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile | Scaling,
                    new[] { "image/jpeg" }, new[] { "jpg", "jpeg" }),
                new PixelManCodecInfo("TIFF", PmCodecKind.Tiff, BitmapContainerFormats.Tiff,
                    Common | BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.ColorProfile |
                    BitmapCodecCapabilities.HighDepth | BitmapCodecCapabilities.Palette |
                    BitmapCodecCapabilities.MultiFrame | BitmapCodecCapabilities.MultiFrameEncode,
                    new[] { "image/tiff" }, new[] { "tif", "tiff" }),
                new PixelManCodecInfo("GIF", PmCodecKind.Gif, BitmapContainerFormats.Gif,
                    Common | BitmapCodecCapabilities.Metadata | BitmapCodecCapabilities.Palette |
                    BitmapCodecCapabilities.MultiFrame | BitmapCodecCapabilities.MultiFrameEncode |
                    BitmapCodecCapabilities.Animation,
                    new[] { "image/gif" }, new[] { "gif" }),
                new PixelManCodecInfo("BMP", PmCodecKind.Bmp, BitmapContainerFormats.Bmp,
                    Common | BitmapCodecCapabilities.Palette | BitmapCodecCapabilities.HighDepth,
                    new[] { "image/bmp" }, new[] { "bmp", "dib" }),
                // Decode only. Every ICO entry is a frame, decoded to Bgra32; WebP animation
                // frames come composited, and WebP carries no frame timing PixelMan exposes.
                new PixelManCodecInfo("ICO", PmCodecKind.Ico, BitmapContainerFormats.Ico,
                    BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.MultiFrame,
                    new[] { "image/x-icon", "image/vnd.microsoft.icon" }, new[] { "ico" }),
                new PixelManCodecInfo("WebP", PmCodecKind.Webp, BitmapContainerFormats.Webp,
                    BitmapCodecCapabilities.Decode | BitmapCodecCapabilities.MultiFrame,
                    new[] { "image/webp" }, new[] { "webp" }),
            };

            // The seek requirements come from the library, so they follow PixelMan's codecs.
            var capabilities = PmCapabilities.Create();

            for (var i = 0; i < codecs.Length; i++)
            {
                foreach (var codec in capabilities.Codecs)
                {
                    if (codec.Kind == codecs[i].Kind && codec.WriteRequiresSeek)
                    {
                        codecs[i] = new PixelManCodecInfo(codecs[i].FormatName, codecs[i].Kind, codecs[i].ContainerFormat,
                            codecs[i].Capabilities | BitmapCodecCapabilities.EncodeRequiresSeekableStream,
                            (string[])codecs[i].MimeTypes, (string[])codecs[i].FileExtensions);
                    }
                }
            }

            return codecs;
        }
    }
}
