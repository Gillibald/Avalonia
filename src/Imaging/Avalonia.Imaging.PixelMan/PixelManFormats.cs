using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// Pixel format, palette and error translation between PixelMan and Avalonia.
    /// </summary>
    internal static class PixelManFormats
    {
        public const string BackendName = "PixelMan";

        /// <summary>
        /// Maps a PixelMan format to the Avalonia format with the same memory layout, or
        /// returns false for the formats Avalonia has no equivalent of: the GDI+
        /// linear-light 48/64-bit formats and Bgra1555.
        /// </summary>
        public static bool TryMap(PmPixelFormat format, out PixelFormat pixelFormat, out AlphaFormat alphaFormat)
        {
            alphaFormat = format.HasAlpha
                ? format.HasPremultipliedAlpha ? AlphaFormat.Premul : AlphaFormat.Unpremul
                : AlphaFormat.Opaque;

            PixelFormat? mapped =
                format == PmPixelFormat.Indexed1 ? PixelFormats.Indexed1 :
                format == PmPixelFormat.Indexed2 ? PixelFormats.Indexed2 :
                format == PmPixelFormat.Indexed4 ? PixelFormats.Indexed4 :
                format == PmPixelFormat.Indexed8 ? PixelFormats.Indexed8 :
                format == PmPixelFormat.BlackWhite ? PixelFormats.BlackWhite :
                format == PmPixelFormat.Gray2 ? PixelFormats.Gray2 :
                format == PmPixelFormat.Gray4 ? PixelFormats.Gray4 :
                format == PmPixelFormat.Gray8 ? PixelFormats.Gray8 :
                format == PmPixelFormat.Gray16 ? PixelFormats.Gray16 :
                format == PmPixelFormat.Gray32Float ? PixelFormats.Gray32Float :
                format == PmPixelFormat.Bgr555 ? PixelFormats.Bgr555 :
                format == PmPixelFormat.Bgr565 ? PixelFormats.Bgr565 :
                format == PmPixelFormat.Bgr24 ? PixelFormats.Bgr24 :
                format == PmPixelFormat.Rgb24 ? PixelFormats.Rgb24 :
                format == PmPixelFormat.Bgr32 ? PixelFormats.Bgr32 :
                format == PmPixelFormat.Bgra32 || format == PmPixelFormat.Pbgra32 ? PixelFormats.Bgra8888 :
                format == PmPixelFormat.Rgba32 || format == PmPixelFormat.Prgba32 ? PixelFormats.Rgba8888 :
                format == PmPixelFormat.Cmyk32 ? PixelFormats.Cmyk32 :
                format == PmPixelFormat.Cmyk64 ? PixelFormats.Cmyk64 :
                format == PmPixelFormat.Rgb48 ? PixelFormats.Rgb48 :
                format == PmPixelFormat.Rgba64 || format == PmPixelFormat.Prgba64 ? PixelFormats.Rgba64 :
                format == PmPixelFormat.Rgba64Half ? PixelFormats.Rgba64Half :
                format == PmPixelFormat.Rgb48Half ? PixelFormats.Rgb48Half :
                format == PmPixelFormat.Rgba128Float || format == PmPixelFormat.Prgba128Float ? PixelFormats.Rgba128Float :
                format == PmPixelFormat.Rgb128Float ? PixelFormats.Rgb128Float :
                format == PmPixelFormat.Bgr101010 ? PixelFormats.Bgr101010 :
                null;

            pixelFormat = mapped ?? default;

            if (mapped is null)
                return false;

            if (!mapped.Value.HasAlpha)
                alphaFormat = AlphaFormat.Opaque;

            return true;
        }

        /// <summary>
        /// Returns the format a frame without an Avalonia equivalent is converted to: the
        /// 16-bit linear formats keep their depth, Bgra1555 becomes 8-bit.
        /// </summary>
        public static PmPixelFormat GetFallback(PmPixelFormat format)
            => format.BitsPerPixel > 32 ? PmPixelFormat.Rgba64 : PmPixelFormat.Bgra32;

        /// <summary>
        /// Maps an Avalonia format and alpha format to the PixelMan format with the same
        /// layout, or null when PixelMan has none.
        /// </summary>
        public static PmPixelFormat? ToPixelMan(PixelFormat format, AlphaFormat alphaFormat)
        {
            var premultiplied = alphaFormat == AlphaFormat.Premul;

            return format.FormatEnum switch
            {
                PixelFormatEnum.Indexed1 => PmPixelFormat.Indexed1,
                PixelFormatEnum.Indexed2 => PmPixelFormat.Indexed2,
                PixelFormatEnum.Indexed4 => PmPixelFormat.Indexed4,
                PixelFormatEnum.Indexed8 => PmPixelFormat.Indexed8,
                PixelFormatEnum.BlackWhite => PmPixelFormat.BlackWhite,
                PixelFormatEnum.Gray2 => PmPixelFormat.Gray2,
                PixelFormatEnum.Gray4 => PmPixelFormat.Gray4,
                PixelFormatEnum.Gray8 => PmPixelFormat.Gray8,
                PixelFormatEnum.Gray16 => PmPixelFormat.Gray16,
                PixelFormatEnum.Gray32Float => PmPixelFormat.Gray32Float,
                PixelFormatEnum.Bgr555 => PmPixelFormat.Bgr555,
                PixelFormatEnum.Bgr565 or PixelFormatEnum.Rgb565 => PmPixelFormat.Bgr565,
                PixelFormatEnum.Bgr24 => PmPixelFormat.Bgr24,
                PixelFormatEnum.Rgb24 => PmPixelFormat.Rgb24,
                PixelFormatEnum.Bgr32 => PmPixelFormat.Bgr32,
                PixelFormatEnum.Bgra8888 => premultiplied ? PmPixelFormat.Pbgra32 : PmPixelFormat.Bgra32,
                PixelFormatEnum.Rgba8888 => premultiplied ? PmPixelFormat.Prgba32 : PmPixelFormat.Rgba32,
                PixelFormatEnum.Cmyk32 => PmPixelFormat.Cmyk32,
                PixelFormatEnum.Cmyk64 => PmPixelFormat.Cmyk64,
                PixelFormatEnum.Rgb48 => PmPixelFormat.Rgb48,
                PixelFormatEnum.Rgba64 => premultiplied ? PmPixelFormat.Prgba64 : PmPixelFormat.Rgba64,
                PixelFormatEnum.Rgba64Half => PmPixelFormat.Rgba64Half,
                PixelFormatEnum.Rgb48Half => PmPixelFormat.Rgb48Half,
                PixelFormatEnum.Rgba128Float => premultiplied ? PmPixelFormat.Prgba128Float : PmPixelFormat.Rgba128Float,
                PixelFormatEnum.Rgb128Float => PmPixelFormat.Rgb128Float,
                PixelFormatEnum.Bgr101010 => PmPixelFormat.Bgr101010,
                _ => null,
            };
        }

        public static BitmapPalette ToPalette(PmPalette palette)
        {
            // PixelMan stores entries as BGRA bytes.
            var entries = palette.GetEntries();
            var colors = new Color[entries.Length / 4];

            for (var i = 0; i < colors.Length; i++)
                colors[i] = Color.FromArgb(entries[i * 4 + 3], entries[i * 4 + 2], entries[i * 4 + 1], entries[i * 4]);

            return new BitmapPalette(colors);
        }

        public static PmPalette ToPixelMan(BitmapPalette palette)
        {
            var entries = new byte[palette.Colors.Count * 4];

            for (var i = 0; i < palette.Colors.Count; i++)
            {
                var color = palette.Colors[i];

                entries[i * 4] = color.B;
                entries[i * 4 + 1] = color.G;
                entries[i * 4 + 2] = color.R;
                entries[i * 4 + 3] = color.A;
            }

            return PmPalette.Create(entries);
        }

        /// <summary>
        /// Translates PixelMan's exceptions into the ones the imaging contract specifies;
        /// cancellation and argument errors pass through.
        /// </summary>
        public static Exception Translate(Exception exception) => exception switch
        {
            PmUnknownFormatException => new NotSupportedException(
                $"The '{BackendName}' imaging backend does not recognize the image data.", exception),
            PmImageTooLargeException => new InvalidOperationException(
                $"The image exceeds the configured pixel limit: {exception.Message}", exception),
            PmLibraryFaultedException => new InvalidOperationException(
                $"The '{BackendName}' native library has faulted and cannot be used again in this process.", exception),
            PmCodecException => new InvalidDataException(exception.Message, exception),
            PmException => new InvalidDataException(exception.Message, exception),
            _ => exception,
        };
    }
}
