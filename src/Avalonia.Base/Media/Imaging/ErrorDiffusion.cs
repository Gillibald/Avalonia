using System;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Media.Imaging;

/// <summary>
/// WIC's error diffusion (<c>WICBitmapDitherTypeErrorDiffusion</c>), with the same output as the format
/// converter WPF's <c>FormatConvertedBitmap</c> uses.
/// </summary>
/// <remarks>
/// WIC scans serpentine with row 0 running right to left and spreads the Floyd-Steinberg weights 7, 3, 5 and 1
/// (forward, behind below, below, ahead below). The weighted errors accumulate in sixteenths and are floored
/// when a pixel reads them; the sum is clamped to 0..255 before quantizing. A pixel is quantized through a
/// table of 16 cells per channel (the top four bits), each cell holding the entry nearest its value
/// <c>c * 17</c>. Palette targets diffuse red, green and blue of the straight color; gray targets diffuse the
/// Gray8 conversion. A pixel below the alpha threshold takes the transparent entry but diffuses the error of
/// its nearest color.
/// </remarks>
internal static unsafe class ErrorDiffusion
{
    /// <summary>Whether WIC dithers a conversion to <paramref name="format"/>.</summary>
    public static bool IsTarget(PixelFormat format) => format.IsIndexed ||
        format.FormatEnum is PixelFormatEnum.BlackWhite or PixelFormatEnum.Gray2 or PixelFormatEnum.Gray4;

    public static void Transcode(IntPtr source, PixelSize size, int sourceStride, PixelFormat sourceFormat,
        AlphaFormat sourceAlphaFormat, BitmapPalette? sourcePalette, IntPtr dest, int destStride,
        PixelFormat destFormat, BitmapPalette? destPalette, double alphaThresholdPercent)
    {
        var palette = destFormat.IsIndexed;
        var input = palette ? PixelFormats.Bgra8888 : PixelFormats.Gray8;
        var channels = palette ? 4 : 1;
        var inputStride = size.Width * channels;

        using var staging = BitmapMemoryPool.Shared.Rent((long)inputStride * size.Height);

        PixelFormatTranscoder.Transcode(source, size, sourceStride, sourceFormat, sourceAlphaFormat, staging.Address,
            inputStride, input, AlphaFormat.Unpremul, sourcePalette, null, 0);

        var diffuser = palette
            ? Diffuser.ForPalette(size.Width, destFormat, destPalette ??
                throw new ArgumentException($"An indexed destination ({destFormat}) needs a palette.", nameof(destPalette)),
                alphaThresholdPercent)
            : Diffuser.ForGray(size.Width, destFormat);

        for (var y = 0; y < size.Height; y++)
        {
            var pixels = new ReadOnlySpan<byte>((byte*)staging.Address + (nint)inputStride * y, inputStride);
            var row = new Span<byte>((byte*)dest + (nint)destStride * y,
                PixelFormatArithmetic.GetMinRowBytes(destFormat, size.Width));

            diffuser.Run(y, pixels, row);
        }
    }

    private sealed class Diffuser
    {
        private readonly int _width;
        private readonly int _channels;
        private readonly int _bits;
        private readonly byte[] _table;
        private readonly int[][] _colors;
        private readonly int _alphaThreshold;
        private readonly int _transparentIndex;
        private int[] _current;
        private int[] _next;

        private Diffuser(int width, int channels, int bits, byte[] table, int[][] colors, int alphaThreshold,
            int transparentIndex)
        {
            _width = width;
            _channels = channels;
            _bits = bits;
            _table = table;
            _colors = colors;
            _alphaThreshold = alphaThreshold;
            _transparentIndex = transparentIndex;

            // One pixel of padding at each end absorbs the error that leaves the image.
            _current = new int[(width + 2) * channels];
            _next = new int[(width + 2) * channels];
        }

        public static Diffuser ForPalette(int width, PixelFormat format, BitmapPalette palette, double alphaThresholdPercent)
        {
            var matcher = new PaletteMatcher(palette, format, alphaThresholdPercent);
            var table = new byte[4096];

            for (var cell = 0; cell < table.Length; cell++)
            {
                table[cell] = (byte)matcher.Nearest(new Rgba8888Pixel
                {
                    R = (byte)((cell >> 8) * 17),
                    G = (byte)(((cell >> 4) & 15) * 17),
                    B = (byte)((cell & 15) * 17),
                    A = 255,
                });
            }

            var entries = matcher.Entries;
            var colors = new int[entries.Length][];

            for (var i = 0; i < entries.Length; i++)
                colors[i] = new int[] { entries[i].R, entries[i].G, entries[i].B };

            return new Diffuser(width, 3, format.BitsPerPixel, table, colors, matcher.AlphaThreshold,
                matcher.TransparentIndex);
        }

        public static Diffuser ForGray(int width, PixelFormat format)
        {
            var levels = (1 << format.BitsPerPixel) - 1;
            var step = 255 / levels;
            var table = new byte[16];

            for (var cell = 0; cell < table.Length; cell++)
            {
                var best = 0;

                for (var q = 1; q <= levels; q++)
                {
                    if (Math.Abs(cell * 17 - q * step) < Math.Abs(cell * 17 - best * step))
                        best = q;
                }

                table[cell] = (byte)best;
            }

            var colors = new int[levels + 1][];

            for (var q = 0; q <= levels; q++)
                colors[q] = new[] { q * step };

            return new Diffuser(width, 1, format.BitsPerPixel, table, colors, 0, 0);
        }

        public void Run(int y, ReadOnlySpan<byte> pixels, Span<byte> row)
        {
            row.Clear();

            var channels = _channels;
            var reverse = y % 2 == 0;
            Span<int> value = stackalloc int[3];

            for (var i = 0; i < _width; i++)
            {
                var x = reverse ? _width - 1 - i : i;
                var slot = (x + 1) * channels;
                var alpha = 255;

                if (channels == 1)
                {
                    value[0] = Math.Clamp(pixels[x] + (_current[slot] >> 4), 0, 255);
                }
                else
                {
                    // Bgra8888 memory order is B, G, R, A; the diffusion channels are R, G, B.
                    var p = x * 4;
                    value[0] = Math.Clamp(pixels[p + 2] + (_current[slot] >> 4), 0, 255);
                    value[1] = Math.Clamp(pixels[p + 1] + (_current[slot + 1] >> 4), 0, 255);
                    value[2] = Math.Clamp(pixels[p] + (_current[slot + 2] >> 4), 0, 255);
                    alpha = pixels[p + 3];
                }

                int q = channels == 1
                    ? _table[value[0] >> 4]
                    : _table[((value[0] >> 4) << 8) | ((value[1] >> 4) << 4) | (value[2] >> 4)];

                var index = alpha < _alphaThreshold ? _transparentIndex : q;
                var shift = 8 - _bits * (x % (8 / _bits) + 1);

                row[x * _bits / 8] |= (byte)(index << shift);

                var forward = reverse ? slot - channels : slot + channels;
                var behind = reverse ? slot + channels : slot - channels;
                var color = _colors[q];

                for (var c = 0; c < channels; c++)
                {
                    var error = value[c] - color[c];

                    _current[forward + c] += 7 * error;
                    _next[behind + c] += 3 * error;
                    _next[slot + c] += 5 * error;
                    _next[forward + c] += error;
                }
            }

            (_current, _next) = (_next, _current);
            Array.Clear(_next);
        }
    }
}
