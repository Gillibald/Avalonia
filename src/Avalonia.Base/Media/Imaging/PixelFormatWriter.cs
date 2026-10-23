using System;
using Avalonia.Platform;
namespace Avalonia.Media.Imaging;

internal interface IPixelFormatWriter
{
    void WriteNext(Rgba8888Pixel pixel);
    void Reset(IntPtr address);
}

internal static unsafe class PixelFormatWriter
{
    public unsafe struct Rgb24PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            addr[0] = pixel.R;
            addr[1] = pixel.G;
            addr[2] = pixel.B;

            _address += 3;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Rgb32PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var address = _address;

            address[0] = pixel.R;
            address[1] = pixel.G;
            address[2] = pixel.B;
            address[3] = 255;

            _address += 4;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Rgba8888PixelFormatWriter : IPixelFormatWriter
    {
        private Rgba8888Pixel* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            *addr = pixel;

            _address++;
        }

        public void Reset(IntPtr address) => _address = (Rgba8888Pixel*)address;
    }

    public unsafe struct Bgra8888PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            addr[0] = pixel.B;
            addr[1] = pixel.G;
            addr[2] = pixel.R;
            addr[3] = pixel.A;

            _address += 4;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Bgr24PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            addr[2] = pixel.R;
            addr[1] = pixel.G;
            addr[0] = pixel.B;

            _address += 3;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Bgr32PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var address = _address;

            address[0] = pixel.B;
            address[1] = pixel.G;
            address[2] = pixel.R;
            address[3] = 255;

            _address += 4;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Bgra32PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            addr[3] = pixel.A;
            addr[2] = pixel.R;
            addr[1] = pixel.G;
            addr[0] = pixel.B;

            _address += 4;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    public unsafe struct Bgr565PixelFormatWriter : IPixelFormatWriter
    {
        private ushort* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            *addr = Pack(pixel);

            _address++;
        }

        public void Reset(IntPtr address) => _address = (ushort*)address;

        private static ushort Pack(Rgba8888Pixel pixel)
        {
            return (ushort)((((int)Math.Round(pixel.R / 255F * 31F) & 0x1F) << 11)
                  | (((int)Math.Round(pixel.G / 255F * 63F) & 0x3F) << 5)
                  | ((int)Math.Round(pixel.B / 255F * 31F) & 0x1F));
        }
    }

    public unsafe struct Bgr555PixelFormatWriter : IPixelFormatWriter
    {
        private ushort* _address;
        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            *addr = Pack(pixel);

            _address++;
        }

        public void Reset(IntPtr address) => _address = (ushort*)address;

        private static ushort Pack(Rgba8888Pixel pixel)
        {
            return (ushort)(
              (((int)Math.Round(pixel.R / 255F * 31F) & 0x1F) << 10)
              | (((int)Math.Round(pixel.G / 255F * 31F) & 0x1F) << 5)
              | (((int)Math.Round(pixel.B / 255F * 31F) & 0x1F) << 0));
        }
    }

    public unsafe struct BlackWhitePixelFormatWriter : IPixelFormatWriter
    {
        private int _bit;
        private byte* _address;

        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            var grayscale = Math.Round(0.299F * pixel.R + 0.587F * pixel.G + 0.114F * pixel.B);

            var value = grayscale > 0x7F ? 1 : 0;

            var shift = 7 - _bit;
            var mask = 1 << shift;

            *addr = (byte)((*addr & ~mask) | value << shift);

            _bit++;

            if (_bit == 8)
            {
                _address++;

                _bit = 0;
            }
        }

        // Rows start byte-aligned, so the bit phase resets together with the address.
        public void Reset(IntPtr address)
        {
            _address = (byte*)address;
            _bit = 0;
        }
    }

    public unsafe struct Gray2PixelFormatWriter : IPixelFormatWriter
    {
        private int _bit;
        private byte* _address;

        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;
            var value = 0;

            var grayscale = (byte)Math.Round(0.299F * pixel.R + 0.587F * pixel.G + 0.114F * pixel.B);

            if (grayscale > 0 && grayscale <= 0x55)
            {
                //01
                value = 1;
            }

            if (grayscale > 0x55 && grayscale <= 0xAA)
            {
                //10

                value = 2;
            }

            if (grayscale > 0xAA)
            {
                //11
                value = 3;
            }

            var shift = 6 - _bit;
            var mask = 3 << shift;

            *addr = (byte)((*addr & ~mask) | value << shift);

            _bit += 2;

            if (_bit == 8)
            {
                _address++;
                _bit = 0;
            }
        }

        // Rows start byte-aligned, so the bit phase resets together with the address.
        public void Reset(IntPtr address)
        {
            _address = (byte*)address;
            _bit = 0;
        }
    }

    public unsafe struct Gray4PixelFormatWriter : IPixelFormatWriter
    {
        private int _bit;
        private byte* _address;

        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            var grayscale = (byte)Math.Round(0.299F * pixel.R + 0.587F * pixel.G + 0.114F * pixel.B);

            var value = (byte)(grayscale / 255F * 0xF);

            var shift = 4 - _bit;
            var mask = 0xF << shift;

            *addr = (byte)((*addr & ~mask) | value << shift);

            _bit += 4;

            if (_bit == 8)
            {
                _address++;
                _bit = 0;
            }
        }

        // Rows start byte-aligned, so the bit phase resets together with the address.
        public void Reset(IntPtr address)
        {
            _address = (byte*)address;
            _bit = 0;
        }
    }

    public unsafe struct Gray8PixelFormatWriter : IPixelFormatWriter
    {
        private byte* _address;

        public void WriteNext(Rgba8888Pixel pixel)
        {
            var addr = _address;

            var grayscale = (byte)Math.Round(0.299F * pixel.R + 0.587F * pixel.G + 0.114F * pixel.B);

            *addr = grayscale;

            _address++;
        }

        public void Reset(IntPtr address) => _address = (byte*)address;
    }

    private static void WriteRow<T>(
        ReadOnlySpan<Rgba8888Pixel> pixels,
        IntPtr dest,
        AlphaFormat alphaFormat,
        AlphaFormat srcAlphaFormat) where T : struct, IPixelFormatWriter
    {
        var writer = new T();

        writer.Reset(dest);

        for (var x = 0; x < pixels.Length; x++)
        {
            writer.WriteNext(GetConvertedPixel(pixels[x], srcAlphaFormat, alphaFormat));
        }
    }

    public static Rgba8888Pixel GetConvertedPixel(Rgba8888Pixel pixel, AlphaFormat sourceAlpha, AlphaFormat destAlpha)
    {
        if (sourceAlpha != destAlpha)
        {
            if (sourceAlpha == AlphaFormat.Premul && destAlpha != AlphaFormat.Premul)
            {
                return ConvertFromPremultiplied(pixel);
            }

            if (sourceAlpha != AlphaFormat.Premul && destAlpha == AlphaFormat.Premul)
            {
                return ConvertToPremultiplied(pixel);
            }
        }

        return pixel;
    }

    // Alpha conversion reproduces WIC bit for bit, so pixels converted here match what
    // WPF and the Windows imaging stack produce for the same input.
    private static Rgba8888Pixel ConvertToPremultiplied(Rgba8888Pixel pixel)
    {
        var alpha = pixel.A;

        return new Rgba8888Pixel
        {
            R = Premultiply(pixel.R, alpha),
            G = Premultiply(pixel.G, alpha),
            B = Premultiply(pixel.B, alpha),
            A = alpha
        };
    }

    // Rounds to nearest.
    private static byte Premultiply(byte channel, int alpha) => (byte)((channel * alpha + 127) / 255);

    private static Rgba8888Pixel ConvertFromPremultiplied(Rgba8888Pixel pixel)
    {
        // A transparent pixel carries no colour, and its zero alpha has no reciprocal.
        if (pixel.A == 0)
            return default;

        // WIC scales by a 16.16 fixed-point reciprocal of alpha and truncates, which is
        // why a fully saturated channel can come back one step below 255.
        var reciprocal = (255u << 16) / pixel.A;

        return new Rgba8888Pixel
        {
            R = Unpremultiply(pixel.R, reciprocal),
            G = Unpremultiply(pixel.G, reciprocal),
            B = Unpremultiply(pixel.B, reciprocal),
            A = pixel.A
        };
    }

    // A premultiplied channel above alpha is not valid premultiplied data; clamp it so
    // the byte conversion cannot wrap around.
    private static byte Unpremultiply(byte channel, uint reciprocal)
        => (byte)Math.Min((channel * reciprocal) >> 16, 255u);

    public static void Write(
        ReadOnlySpan<Rgba8888Pixel> pixels,
        IntPtr dest,
        PixelSize size,
        int stride,
        PixelFormat format,
        AlphaFormat alphaFormat,
        AlphaFormat srcAlphaFormat)
    {
        var w = size.Width;
        var h = size.Height;

        for (var y = 0; y < h; y++)
        {
            WriteRow(pixels.Slice(y * w, w), dest + stride * y, format, alphaFormat, srcAlphaFormat);
        }
    }

    public static void WriteRow(
        ReadOnlySpan<Rgba8888Pixel> pixels,
        IntPtr dest,
        PixelFormat format,
        AlphaFormat alphaFormat,
        AlphaFormat srcAlphaFormat)
    {
        switch (format.FormatEnum)
        {
            case PixelFormatEnum.Rgb565:
                WriteRow<Bgr565PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Rgba8888:
                WriteRow<Rgba8888PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Bgra8888:
                WriteRow<Bgra8888PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.BlackWhite:
                WriteRow<BlackWhitePixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Gray2:
                WriteRow<Gray2PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Gray4:
                WriteRow<Gray4PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Gray8:
                WriteRow<Gray8PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Rgb24:
                WriteRow<Rgb24PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Rgb32:
                WriteRow<Rgb32PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Bgr24:
                WriteRow<Bgr24PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Bgr32:
                WriteRow<Bgr32PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Bgr555:
                WriteRow<Bgr555PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            case PixelFormatEnum.Bgr565:
                WriteRow<Bgr565PixelFormatWriter>(pixels, dest, alphaFormat, srcAlphaFormat);
                break;
            default:
                if (format.IsIndexed)
                    throw new NotSupportedException($"Pixel format {format} needs a palette to convert.");

                if (!WidePixelFormat.IsWide(format))
                    throw new NotSupportedException($"Pixel format {format} is not supported");

                WidePixelFormat.WriteNarrowRow(pixels, dest, format, alphaFormat, srcAlphaFormat);
                break;
        }
    }
}


