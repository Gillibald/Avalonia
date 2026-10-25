using System;

namespace Avalonia.Platform
{
    // The numeric values cross process and native boundaries (the remote protocol and
    // the macOS framebuffer cast them to integers), so new members are only appended.
    internal enum PixelFormatEnum
    {
        Rgb565,
        Rgba8888,
        Bgra8888,
        BlackWhite,
        Gray2,
        Gray4,
        Gray8,
        Gray16,
        Gray32Float,
        Rgba64,
        Rgb24,
        Rgb32,
        Bgr24,
        Bgr32,
        Bgr555,
        Bgr565,
        Indexed1,
        Indexed2,
        Indexed4,
        Indexed8,
        Rgb48,
        Bgra64,
        Rgba64Half,
        Rgb48Half,
        Rgba128Float,
        Rgb128Float,
        Cmyk32,
        Cmyk64,
        Bgr101010,
        Bgra5551,
        Bgr48Linear,
        Bgra64Linear
    }

    public record struct PixelFormat
    {
        internal PixelFormatEnum FormatEnum;

        public int BitsPerPixel => FormatEnum switch
        {
            PixelFormatEnum.BlackWhite or PixelFormatEnum.Indexed1 => 1,
            PixelFormatEnum.Gray2 or PixelFormatEnum.Indexed2 => 2,
            PixelFormatEnum.Gray4 or PixelFormatEnum.Indexed4 => 4,
            PixelFormatEnum.Gray8 or PixelFormatEnum.Indexed8 => 8,
            PixelFormatEnum.Rgb565 or PixelFormatEnum.Bgr555 or PixelFormatEnum.Bgr565 or PixelFormatEnum.Gray16
                or PixelFormatEnum.Bgra5551 => 16,
            PixelFormatEnum.Bgr24 or PixelFormatEnum.Rgb24 => 24,
            PixelFormatEnum.Rgb48 or PixelFormatEnum.Rgb48Half or PixelFormatEnum.Bgr48Linear => 48,
            PixelFormatEnum.Rgba64 or PixelFormatEnum.Bgra64 or PixelFormatEnum.Rgba64Half or PixelFormatEnum.Cmyk64
                or PixelFormatEnum.Bgra64Linear => 64,
            PixelFormatEnum.Rgba128Float or PixelFormatEnum.Rgb128Float => 128,
            _ => 32
        };

        /// <summary>
        /// Gets a value indicating whether the format stores an alpha channel. Indexed
        /// formats report false: their transparency lives in the palette.
        /// </summary>
        public bool HasAlpha => FormatEnum is PixelFormatEnum.Rgba8888 or PixelFormatEnum.Bgra8888
            or PixelFormatEnum.Rgba64 or PixelFormatEnum.Bgra64 or PixelFormatEnum.Rgba64Half
            or PixelFormatEnum.Rgba128Float or PixelFormatEnum.Bgra5551 or PixelFormatEnum.Bgra64Linear;

        /// <summary>
        /// Gets a value indicating whether pixels are indices into a palette rather than colors.
        /// </summary>
        public bool IsIndexed => FormatEnum is PixelFormatEnum.Indexed1 or PixelFormatEnum.Indexed2
            or PixelFormatEnum.Indexed4 or PixelFormatEnum.Indexed8;

        /// <summary>
        /// Gets a value indicating whether the format stores a single gray channel.
        /// </summary>
        public bool IsGrayscale => FormatEnum is PixelFormatEnum.BlackWhite or PixelFormatEnum.Gray2
            or PixelFormatEnum.Gray4 or PixelFormatEnum.Gray8 or PixelFormatEnum.Gray16
            or PixelFormatEnum.Gray32Float;

        /// <summary>
        /// Gets a value indicating whether channels are floating-point values. Floating-point
        /// formats hold linear (scRGB) values, as do the fixed-point <see cref="PixelFormats.Bgr48Linear"/>
        /// and <see cref="PixelFormats.Bgra64Linear"/>; all other color formats are sRGB-encoded.
        /// </summary>
        public bool IsFloat => FormatEnum is PixelFormatEnum.Gray32Float or PixelFormatEnum.Rgba64Half
            or PixelFormatEnum.Rgb48Half or PixelFormatEnum.Rgba128Float or PixelFormatEnum.Rgb128Float;

        /// <summary>
        /// Gets a value indicating whether the format stores cyan, magenta, yellow and black ink.
        /// </summary>
        public bool IsCmyk => FormatEnum is PixelFormatEnum.Cmyk32 or PixelFormatEnum.Cmyk64;

        /// <summary>
        /// Gets the number of channels a pixel stores, counting alpha and excluding unused
        /// padding bits. Indexed formats count their palette index as one channel.
        /// </summary>
        public int ChannelCount => FormatEnum switch
        {
            PixelFormatEnum.Rgba8888 or PixelFormatEnum.Bgra8888 or PixelFormatEnum.Rgba64
                or PixelFormatEnum.Bgra64 or PixelFormatEnum.Rgba64Half or PixelFormatEnum.Rgba128Float
                or PixelFormatEnum.Cmyk32 or PixelFormatEnum.Cmyk64 or PixelFormatEnum.Bgra5551
                or PixelFormatEnum.Bgra64Linear => 4,
            PixelFormatEnum.Rgb565 or PixelFormatEnum.Rgb24 or PixelFormatEnum.Rgb32 or PixelFormatEnum.Bgr24
                or PixelFormatEnum.Bgr32 or PixelFormatEnum.Bgr555 or PixelFormatEnum.Bgr565
                or PixelFormatEnum.Rgb48 or PixelFormatEnum.Rgb48Half or PixelFormatEnum.Rgb128Float
                or PixelFormatEnum.Bgr101010 or PixelFormatEnum.Bgr48Linear => 3,
            _ => 1
        };

        internal PixelFormat(PixelFormatEnum format)
        {
            FormatEnum = format;
        }

        public static PixelFormat Rgb565 => PixelFormats.Rgb565;
        public static PixelFormat Rgba8888 => PixelFormats.Rgba8888;
        public static PixelFormat Rgb32 => PixelFormats.Rgb32;
        public static PixelFormat Bgra8888 => PixelFormats.Bgra8888;

        public override string ToString() => FormatEnum.ToString();
    }

    public static class PixelFormats
    {
        public static PixelFormat Rgb565 { get; } = new PixelFormat(PixelFormatEnum.Rgb565);
        public static PixelFormat Rgba8888 { get; } = new PixelFormat(PixelFormatEnum.Rgba8888);
        public static PixelFormat Rgba64 { get; } = new PixelFormat(PixelFormatEnum.Rgba64);
        public static PixelFormat Bgra8888 { get; } = new PixelFormat(PixelFormatEnum.Bgra8888);
        public static PixelFormat BlackWhite { get; } = new PixelFormat(PixelFormatEnum.BlackWhite);
        public static PixelFormat Gray2 { get; } = new PixelFormat(PixelFormatEnum.Gray2);
        public static PixelFormat Gray4 { get; } = new PixelFormat(PixelFormatEnum.Gray4);
        public static PixelFormat Gray8 { get; } = new PixelFormat(PixelFormatEnum.Gray8);
        public static PixelFormat Gray16 { get; } = new PixelFormat(PixelFormatEnum.Gray16);
        public static PixelFormat Gray32Float { get; } = new PixelFormat(PixelFormatEnum.Gray32Float);
        public static PixelFormat Rgb24 { get; } = new PixelFormat(PixelFormatEnum.Rgb24);
        public static PixelFormat Rgb32 { get; } = new PixelFormat(PixelFormatEnum.Rgb32);
        public static PixelFormat Bgr24 { get; } = new PixelFormat(PixelFormatEnum.Bgr24);
        public static PixelFormat Bgr32 { get; } = new PixelFormat(PixelFormatEnum.Bgr32);
        public static PixelFormat Bgr555 { get; } = new PixelFormat(PixelFormatEnum.Bgr555);
        public static PixelFormat Bgr565 { get; } = new PixelFormat(PixelFormatEnum.Bgr565);

        /// <summary>
        /// One-bit palette indices, eight pixels per byte, most significant bit first.
        /// </summary>
        public static PixelFormat Indexed1 { get; } = new PixelFormat(PixelFormatEnum.Indexed1);

        /// <summary>
        /// Two-bit palette indices, four pixels per byte, most significant bits first.
        /// </summary>
        public static PixelFormat Indexed2 { get; } = new PixelFormat(PixelFormatEnum.Indexed2);

        /// <summary>
        /// Four-bit palette indices, two pixels per byte, most significant bits first.
        /// </summary>
        public static PixelFormat Indexed4 { get; } = new PixelFormat(PixelFormatEnum.Indexed4);

        /// <summary>
        /// Eight-bit palette indices, one pixel per byte.
        /// </summary>
        public static PixelFormat Indexed8 { get; } = new PixelFormat(PixelFormatEnum.Indexed8);

        /// <summary>
        /// Red, green and blue as 16-bit little-endian unsigned integers, in that order.
        /// </summary>
        public static PixelFormat Rgb48 { get; } = new PixelFormat(PixelFormatEnum.Rgb48);

        /// <summary>
        /// Blue, green, red and alpha as 16-bit little-endian unsigned integers, in that order.
        /// </summary>
        public static PixelFormat Bgra64 { get; } = new PixelFormat(PixelFormatEnum.Bgra64);

        /// <summary>
        /// Red, green, blue and alpha as IEEE 754 half-precision values holding linear (scRGB) color.
        /// </summary>
        public static PixelFormat Rgba64Half { get; } = new PixelFormat(PixelFormatEnum.Rgba64Half);

        /// <summary>
        /// Red, green and blue as IEEE 754 half-precision values holding linear (scRGB) color.
        /// </summary>
        public static PixelFormat Rgb48Half { get; } = new PixelFormat(PixelFormatEnum.Rgb48Half);

        /// <summary>
        /// Red, green, blue and alpha as 32-bit floats holding linear (scRGB) color.
        /// </summary>
        public static PixelFormat Rgba128Float { get; } = new PixelFormat(PixelFormatEnum.Rgba128Float);

        /// <summary>
        /// Red, green and blue as 32-bit floats holding linear (scRGB) color, followed by 32 unused bits.
        /// </summary>
        public static PixelFormat Rgb128Float { get; } = new PixelFormat(PixelFormatEnum.Rgb128Float);

        /// <summary>
        /// Cyan, magenta, yellow and black as 8-bit ink amounts, where 0 is no ink.
        /// </summary>
        public static PixelFormat Cmyk32 { get; } = new PixelFormat(PixelFormatEnum.Cmyk32);

        /// <summary>
        /// Cyan, magenta, yellow and black as 16-bit little-endian ink amounts, where 0 is no ink.
        /// </summary>
        public static PixelFormat Cmyk64 { get; } = new PixelFormat(PixelFormatEnum.Cmyk64);

        /// <summary>
        /// A 32-bit little-endian word holding 10-bit blue in bits 0-9, green in bits 10-19
        /// and red in bits 20-29; the top two bits are unused.
        /// </summary>
        public static PixelFormat Bgr101010 { get; } = new PixelFormat(PixelFormatEnum.Bgr101010);

        /// <summary>
        /// A 16-bit little-endian word holding 5-bit blue in bits 0-4, green in bits 5-9, red in
        /// bits 10-14 and a one-bit alpha in bit 15 (GDI+ <c>Format16bppArgb1555</c>).
        /// </summary>
        public static PixelFormat Bgra5551 { get; } = new PixelFormat(PixelFormatEnum.Bgra5551);

        /// <summary>
        /// Blue, green and red as 16-bit little-endian signed fixed-point values with 13 fraction
        /// bits, 8192 for 1.0, in linear light (GDI+ <c>Format48bppRgb</c>).
        /// </summary>
        public static PixelFormat Bgr48Linear { get; } = new PixelFormat(PixelFormatEnum.Bgr48Linear);

        /// <summary>
        /// Blue, green, red and alpha as 16-bit little-endian signed fixed-point values with 13
        /// fraction bits, 8192 for 1.0, with colors in linear light (GDI+ <c>Format64bppArgb</c>,
        /// or <c>Format64bppPArgb</c> with premultiplied alpha).
        /// </summary>
        public static PixelFormat Bgra64Linear { get; } = new PixelFormat(PixelFormatEnum.Bgra64Linear);
    }
}
