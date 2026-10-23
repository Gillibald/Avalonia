using System;
using System.Numerics;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Reads and writes the formats whose channels do not fit in eight bits, are floating
/// point, or are not RGB, through a wide row of one <see cref="Vector4"/> per pixel.
/// </summary>
/// <remarks>
/// A wide pixel holds red, green, blue and alpha in X, Y, Z and W as sRGB-encoded values
/// with straight (unassociated) alpha; 0 to 1 covers the displayable range. Integer
/// formats store sRGB-encoded values, so widening them is exact; floating-point formats
/// store linear (scRGB) values and pass through the sRGB transfer curve. Premultiplied
/// formats are converted to straight alpha in their own space on read and back on write.
/// </remarks>
internal static unsafe class WidePixelFormat
{
    public static bool IsWide(PixelFormat format) => format.FormatEnum is PixelFormatEnum.Gray16
        or PixelFormatEnum.Gray32Float or PixelFormatEnum.Rgba64 or PixelFormatEnum.Rgb48
        or PixelFormatEnum.Bgra64 or PixelFormatEnum.Rgba64Half or PixelFormatEnum.Rgb48Half
        or PixelFormatEnum.Rgba128Float or PixelFormatEnum.Rgb128Float or PixelFormatEnum.Cmyk32
        or PixelFormatEnum.Cmyk64 or PixelFormatEnum.Bgr101010;

    /// <summary>
    /// Reads one row of a wide format into straight-alpha wide pixels.
    /// </summary>
    public static void ReadRow(IntPtr source, PixelFormat format, AlphaFormat alphaFormat, Span<Vector4> row)
    {
        var premultiplied = alphaFormat == AlphaFormat.Premul && format.HasAlpha;

        switch (format.FormatEnum)
        {
            case PixelFormatEnum.Gray16:
            {
                var src = (ushort*)source;

                for (var x = 0; x < row.Length; x++)
                {
                    var gray = src[x] / 65535f;

                    row[x] = new Vector4(gray, gray, gray, 1);
                }

                break;
            }
            case PixelFormatEnum.Gray32Float:
            {
                var src = (float*)source;

                for (var x = 0; x < row.Length; x++)
                {
                    var gray = LinearToSrgb(src[x]);

                    row[x] = new Vector4(gray, gray, gray, 1);
                }

                break;
            }
            case PixelFormatEnum.Rgb48:
            {
                var src = (ushort*)source;

                for (var x = 0; x < row.Length; x++, src += 3)
                    row[x] = new Vector4(src[0] / 65535f, src[1] / 65535f, src[2] / 65535f, 1);

                break;
            }
            case PixelFormatEnum.Rgba64:
            {
                var src = (ushort*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = Straighten(new Vector4(src[0], src[1], src[2], src[3]) / 65535f, premultiplied);

                break;
            }
            case PixelFormatEnum.Bgra64:
            {
                var src = (ushort*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = Straighten(new Vector4(src[2], src[1], src[0], src[3]) / 65535f, premultiplied);

                break;
            }
            case PixelFormatEnum.Rgb48Half:
            {
                var src = (Half*)source;

                for (var x = 0; x < row.Length; x++, src += 3)
                    row[x] = FromLinear(new Vector4((float)src[0], (float)src[1], (float)src[2], 1), false);

                break;
            }
            case PixelFormatEnum.Rgba64Half:
            {
                var src = (Half*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = FromLinear(new Vector4((float)src[0], (float)src[1], (float)src[2], (float)src[3]), premultiplied);

                break;
            }
            case PixelFormatEnum.Rgb128Float:
            {
                var src = (float*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = FromLinear(new Vector4(src[0], src[1], src[2], 1), false);

                break;
            }
            case PixelFormatEnum.Rgba128Float:
            {
                var src = (Vector4*)source;

                for (var x = 0; x < row.Length; x++)
                    row[x] = FromLinear(src[x], premultiplied);

                break;
            }
            case PixelFormatEnum.Cmyk32:
            {
                var src = (byte*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = FromCmyk(src[0] / 255f, src[1] / 255f, src[2] / 255f, src[3] / 255f);

                break;
            }
            case PixelFormatEnum.Cmyk64:
            {
                var src = (ushort*)source;

                for (var x = 0; x < row.Length; x++, src += 4)
                    row[x] = FromCmyk(src[0] / 65535f, src[1] / 65535f, src[2] / 65535f, src[3] / 65535f);

                break;
            }
            case PixelFormatEnum.Bgr101010:
            {
                var src = (uint*)source;

                for (var x = 0; x < row.Length; x++)
                {
                    var word = src[x];

                    row[x] = new Vector4(((word >> 20) & 0x3FF) / 1023f, ((word >> 10) & 0x3FF) / 1023f,
                        (word & 0x3FF) / 1023f, 1);
                }

                break;
            }
            default:
                throw new NotSupportedException($"Pixel format {format} is not supported");
        }
    }

    /// <summary>
    /// Writes straight-alpha wide pixels into one row of a wide format.
    /// </summary>
    public static void WriteRow(ReadOnlySpan<Vector4> row, IntPtr dest, PixelFormat format, AlphaFormat alphaFormat)
    {
        var premultiplied = alphaFormat == AlphaFormat.Premul && format.HasAlpha;

        switch (format.FormatEnum)
        {
            case PixelFormatEnum.Gray16:
            {
                var dst = (ushort*)dest;

                for (var x = 0; x < row.Length; x++)
                    dst[x] = ToUInt16(Luminance(row[x]));

                break;
            }
            case PixelFormatEnum.Gray32Float:
            {
                var dst = (float*)dest;

                for (var x = 0; x < row.Length; x++)
                    dst[x] = SrgbToLinear(Luminance(row[x]));

                break;
            }
            case PixelFormatEnum.Rgb48:
            {
                var dst = (ushort*)dest;

                for (var x = 0; x < row.Length; x++, dst += 3)
                {
                    var pixel = row[x];

                    dst[0] = ToUInt16(pixel.X);
                    dst[1] = ToUInt16(pixel.Y);
                    dst[2] = ToUInt16(pixel.Z);
                }

                break;
            }
            case PixelFormatEnum.Rgba64:
            {
                var dst = (ushort*)dest;

                for (var x = 0; x < row.Length; x++, dst += 4)
                {
                    var pixel = Associate(row[x], premultiplied);

                    dst[0] = ToUInt16(pixel.X);
                    dst[1] = ToUInt16(pixel.Y);
                    dst[2] = ToUInt16(pixel.Z);
                    dst[3] = ToUInt16(pixel.W);
                }

                break;
            }
            case PixelFormatEnum.Bgra64:
            {
                var dst = (ushort*)dest;

                for (var x = 0; x < row.Length; x++, dst += 4)
                {
                    var pixel = Associate(row[x], premultiplied);

                    dst[0] = ToUInt16(pixel.Z);
                    dst[1] = ToUInt16(pixel.Y);
                    dst[2] = ToUInt16(pixel.X);
                    dst[3] = ToUInt16(pixel.W);
                }

                break;
            }
            case PixelFormatEnum.Rgb48Half:
            {
                var dst = (Half*)dest;

                for (var x = 0; x < row.Length; x++, dst += 3)
                {
                    var pixel = ToLinear(row[x], false);

                    dst[0] = (Half)pixel.X;
                    dst[1] = (Half)pixel.Y;
                    dst[2] = (Half)pixel.Z;
                }

                break;
            }
            case PixelFormatEnum.Rgba64Half:
            {
                var dst = (Half*)dest;

                for (var x = 0; x < row.Length; x++, dst += 4)
                {
                    var pixel = ToLinear(row[x], premultiplied);

                    dst[0] = (Half)pixel.X;
                    dst[1] = (Half)pixel.Y;
                    dst[2] = (Half)pixel.Z;
                    dst[3] = (Half)pixel.W;
                }

                break;
            }
            case PixelFormatEnum.Rgb128Float:
            {
                var dst = (Vector4*)dest;

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = ToLinear(row[x], false);

                    dst[x] = new Vector4(pixel.X, pixel.Y, pixel.Z, 0);
                }

                break;
            }
            case PixelFormatEnum.Rgba128Float:
            {
                var dst = (Vector4*)dest;

                for (var x = 0; x < row.Length; x++)
                    dst[x] = ToLinear(row[x], premultiplied);

                break;
            }
            case PixelFormatEnum.Cmyk32:
            {
                var dst = (byte*)dest;

                for (var x = 0; x < row.Length; x++, dst += 4)
                {
                    var ink = ToCmyk(row[x]);

                    dst[0] = ToByte(ink.X);
                    dst[1] = ToByte(ink.Y);
                    dst[2] = ToByte(ink.Z);
                    dst[3] = ToByte(ink.W);
                }

                break;
            }
            case PixelFormatEnum.Cmyk64:
            {
                var dst = (ushort*)dest;

                for (var x = 0; x < row.Length; x++, dst += 4)
                {
                    var ink = ToCmyk(row[x]);

                    dst[0] = ToUInt16(ink.X);
                    dst[1] = ToUInt16(ink.Y);
                    dst[2] = ToUInt16(ink.Z);
                    dst[3] = ToUInt16(ink.W);
                }

                break;
            }
            case PixelFormatEnum.Bgr101010:
            {
                var dst = (uint*)dest;

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];

                    // WIC sets the two unused bits; matching it keeps encoded files identical.
                    dst[x] = ToUInt10(pixel.Z) | (ToUInt10(pixel.Y) << 10) | (ToUInt10(pixel.X) << 20) | (3u << 30);
                }

                break;
            }
            default:
                throw new NotSupportedException($"Pixel format {format} is not supported");
        }
    }

    /// <summary>
    /// Reads one row of a wide format as 8-bit pixels for consumers of the 8-bit row. As
    /// for every 8-bit reader, alpha keeps the source interpretation.
    /// </summary>
    /// <remarks>
    /// Premultiplied floating-point pixels are encoded without being straightened first,
    /// so their color is approximate; the wide row is exact.
    /// </remarks>
    public static void ReadNarrowRow(Span<Rgba8888Pixel> pixels, IntPtr source, PixelFormat format)
    {
        using var staging = BitmapMemoryPool.Shared.Rent((long)pixels.Length * sizeof(Vector4));

        var row = new Span<Vector4>((void*)staging.Address, pixels.Length);

        ReadRow(source, format, AlphaFormat.Unpremul, row);
        Narrow(row, pixels);
    }

    /// <summary>
    /// Writes 8-bit pixels into one row of a wide format, converting each pixel from
    /// <paramref name="rowAlphaFormat"/> to <paramref name="alphaFormat"/> first, as every
    /// 8-bit writer does.
    /// </summary>
    public static void WriteNarrowRow(ReadOnlySpan<Rgba8888Pixel> pixels, IntPtr dest, PixelFormat format,
        AlphaFormat alphaFormat, AlphaFormat rowAlphaFormat)
    {
        using var staging = BitmapMemoryPool.Shared.Rent((long)pixels.Length * sizeof(Vector4));

        var row = new Span<Vector4>((void*)staging.Address, pixels.Length);

        for (var x = 0; x < pixels.Length; x++)
        {
            var pixel = PixelFormatWriter.GetConvertedPixel(pixels[x], rowAlphaFormat, alphaFormat);

            row[x] = new Vector4(pixel.R, pixel.G, pixel.B, pixel.A) / 255f;
        }

        WriteRow(row, dest, format, AlphaFormat.Unpremul);
    }

    /// <summary>
    /// Expands 8-bit pixels into straight-alpha wide pixels.
    /// </summary>
    public static void Widen(ReadOnlySpan<Rgba8888Pixel> pixels, AlphaFormat alphaFormat, Span<Vector4> row)
    {
        for (var x = 0; x < pixels.Length; x++)
        {
            var pixel = PixelFormatWriter.GetConvertedPixel(pixels[x], alphaFormat, AlphaFormat.Unpremul);

            row[x] = new Vector4(pixel.R, pixel.G, pixel.B, pixel.A) / 255f;
        }
    }

    /// <summary>
    /// Rounds straight-alpha wide pixels to 8-bit pixels with straight alpha.
    /// </summary>
    public static void Narrow(ReadOnlySpan<Vector4> row, Span<Rgba8888Pixel> pixels)
    {
        for (var x = 0; x < row.Length; x++)
        {
            var pixel = row[x];

            pixels[x] = new Rgba8888Pixel(ToByte(pixel.X), ToByte(pixel.Y), ToByte(pixel.Z), ToByte(pixel.W));
        }
    }

    /// <summary>
    /// Converts a linear (scRGB) channel to sRGB encoding. Values outside 0 to 1 follow the
    /// curve mirrored around zero, as scRGB's extended range does.
    /// </summary>
    public static float LinearToSrgb(float value)
    {
        var magnitude = MathF.Abs(value);
        var encoded = magnitude <= 0.0031308f ? magnitude * 12.92f : 1.055f * MathF.Pow(magnitude, 1 / 2.4f) - 0.055f;

        return value < 0 ? -encoded : encoded;
    }

    /// <summary>
    /// Converts an sRGB-encoded channel to linear (scRGB).
    /// </summary>
    public static float SrgbToLinear(float value)
    {
        var magnitude = MathF.Abs(value);
        var linear = magnitude <= 0.04045f ? magnitude / 12.92f : MathF.Pow((magnitude + 0.055f) / 1.055f, 2.4f);

        return value < 0 ? -linear : linear;
    }

    // Matches the 8-bit gray writers, which weight the sRGB-encoded channels.
    private static float Luminance(Vector4 pixel) => 0.299f * pixel.X + 0.587f * pixel.Y + 0.114f * pixel.Z;

    private static Vector4 Straighten(Vector4 pixel, bool premultiplied)
    {
        if (!premultiplied)
            return pixel;

        if (pixel.W <= 0)
            return Vector4.Zero;

        return new Vector4(pixel.X / pixel.W, pixel.Y / pixel.W, pixel.Z / pixel.W, pixel.W);
    }

    private static Vector4 Associate(Vector4 pixel, bool premultiplied)
        => premultiplied ? new Vector4(pixel.X * pixel.W, pixel.Y * pixel.W, pixel.Z * pixel.W, pixel.W) : pixel;

    private static Vector4 FromLinear(Vector4 pixel, bool premultiplied)
    {
        pixel = Straighten(pixel, premultiplied);

        return new Vector4(LinearToSrgb(pixel.X), LinearToSrgb(pixel.Y), LinearToSrgb(pixel.Z), pixel.W);
    }

    private static Vector4 ToLinear(Vector4 pixel, bool premultiplied)
        => Associate(new Vector4(SrgbToLinear(pixel.X), SrgbToLinear(pixel.Y), SrgbToLinear(pixel.Z), pixel.W),
            premultiplied);

    // Without an ICC profile, ink converts by the device-independent complement; color
    // managed conversion is the job of a color transform.
    private static Vector4 FromCmyk(float cyan, float magenta, float yellow, float black)
    {
        var white = 1 - black;

        return new Vector4((1 - cyan) * white, (1 - magenta) * white, (1 - yellow) * white, 1);
    }

    private static Vector4 ToCmyk(Vector4 pixel)
    {
        var red = Math.Clamp(pixel.X, 0, 1);
        var green = Math.Clamp(pixel.Y, 0, 1);
        var blue = Math.Clamp(pixel.Z, 0, 1);
        var white = MathF.Max(red, MathF.Max(green, blue));

        if (white <= 0)
            return new Vector4(0, 0, 0, 1);

        return new Vector4((white - red) / white, (white - green) / white, (white - blue) / white, 1 - white);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);

    private static ushort ToUInt16(float value) => (ushort)Math.Clamp(MathF.Round(value * 65535), 0, 65535);

    // WIC narrows ten-bit channels from their sixteen-bit value, which turns an 8-bit
    // source into exact bit replication; rounding at ten bits differs on a sixth of them.
    private static uint ToUInt10(float value) => (uint)ToUInt16(value) >> 6;
}
