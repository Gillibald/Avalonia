using System;
using System.Buffers;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Scales a premultiplied BGRA strike bitmap to the size a glyph is drawn at, one axis after
    /// the other: an axis that shrinks averages the source span each destination pixel covers,
    /// each texel weighted by its overlap; an axis that grows interpolates linearly between the
    /// two texel centres nearest to the destination pixel centre, held inside the outermost
    /// centres. Weights are fixed point, the same for every channel, so premultiplied texels stay
    /// valid and the result does not depend on the platform.
    /// </summary>
    internal static class BitmapGlyphScaler
    {
        // Tap weights of one axis sum to One.
        private const int WeightBits = 14;
        private const int One = 1 << WeightBits;

        // The horizontal pass keeps this many bits below a level, so the vertical pass rounds once.
        private const int IntermediateBits = 8;
        private const int HorizontalShift = WeightBits - IntermediateBits;
        private const int VerticalShift = WeightBits + IntermediateBits;

        /// <summary>
        /// Writes <paramref name="source"/> scaled to <paramref name="width"/> by
        /// <paramref name="height"/> into <paramref name="destination"/>, rows
        /// <c>width * 4</c> bytes apart.
        /// </summary>
        public static void Scale(in DecodedGlyphBitmap source, int width, int height, Span<byte> destination)
        {
            var columns = Taps.Create(source.Width, width);
            var rows = Taps.Create(source.Height, height);

            // Each source row scaled horizontally, in levels times 2^8.
            var length = source.Height * width * 4;
            var intermediate = ArrayPool<ushort>.Shared.Rent(length);
            var bgra = source.Bgra;

            try
            {
                ScaleRows(bgra, source.Width, source.Height, width, columns, intermediate);
                ScaleColumns(intermediate, width, height, rows, destination);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(intermediate);
            }
        }

        private static void ScaleRows(byte[] bgra, int sourceWidth, int sourceHeight, int width, in Taps columns,
            ushort[] intermediate)
        {
            for (var y = 0; y < sourceHeight; y++)
            {
                var sourceRow = y * sourceWidth * 4;
                var targetRow = y * width * 4;

                for (var x = 0; x < width; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0;
                    var first = columns.First[x];
                    var tap = columns.Offset[x];
                    var end = columns.Offset[x + 1];

                    for (var i = sourceRow + first * 4; tap < end; tap++, i += 4)
                    {
                        var w = columns.Weights[tap];

                        b += w * bgra[i];
                        g += w * bgra[i + 1];
                        r += w * bgra[i + 2];
                        a += w * bgra[i + 3];
                    }

                    var t = targetRow + x * 4;

                    intermediate[t] = (ushort)((b + (1 << (HorizontalShift - 1))) >> HorizontalShift);
                    intermediate[t + 1] = (ushort)((g + (1 << (HorizontalShift - 1))) >> HorizontalShift);
                    intermediate[t + 2] = (ushort)((r + (1 << (HorizontalShift - 1))) >> HorizontalShift);
                    intermediate[t + 3] = (ushort)((a + (1 << (HorizontalShift - 1))) >> HorizontalShift);
                }
            }
        }

        private static void ScaleColumns(ushort[] intermediate, int width, int height, in Taps rows,
            Span<byte> destination)
        {
            var rowBytes = width * 4;

            for (var y = 0; y < height; y++)
            {
                var first = rows.First[y];
                var start = rows.Offset[y];
                var end = rows.Offset[y + 1];
                var target = destination.Slice(y * rowBytes, rowBytes);

                for (var c = 0; c < rowBytes; c++)
                {
                    var sum = 0;
                    var i = first * rowBytes + c;

                    for (var tap = start; tap < end; tap++, i += rowBytes)
                    {
                        sum += rows.Weights[tap] * intermediate[i];
                    }

                    var value = (sum + (1 << (VerticalShift - 1))) >> VerticalShift;

                    target[c] = (byte)(value > 255 ? 255 : value);
                }
            }
        }

        /// <summary>
        /// The source texels each destination index of one axis reads: from <see cref="First"/>
        /// on, one weight per texel in <see cref="Weights"/> between <see cref="Offset"/> and the
        /// next index's offset.
        /// </summary>
        internal readonly struct Taps
        {
            private Taps(int[] first, int[] offset, int[] weights)
            {
                First = first;
                Offset = offset;
                Weights = weights;
            }

            public int[] First { get; }

            public int[] Offset { get; }

            public int[] Weights { get; }

            public static Taps Create(int sourceLength, int destinationLength)
            {
                var first = new int[destinationLength];
                var offset = new int[destinationLength + 1];
                var weights = new System.Collections.Generic.List<int>(destinationLength * 2);
                var scale = (double)sourceLength / destinationLength;

                for (var d = 0; d < destinationLength; d++)
                {
                    offset[d] = weights.Count;

                    if (destinationLength < sourceLength)
                    {
                        // The span the pixel covers, each texel weighted by its overlap.
                        var left = d * scale;
                        var right = (d + 1) * scale;
                        var start = (int)Math.Floor(left);
                        var stop = Math.Min(sourceLength, (int)Math.Ceiling(right));
                        var total = 0;
                        var largest = weights.Count;

                        first[d] = start;

                        for (var s = start; s < stop; s++)
                        {
                            var w = (int)Math.Round((Math.Min(right, s + 1) - Math.Max(left, s)) / scale * One);

                            weights.Add(w);
                            total += w;

                            if (w > weights[largest])
                            {
                                largest = weights.Count - 1;
                            }
                        }

                        // Rounding error goes to the heaviest tap, so the weights sum to One.
                        weights[largest] += One - total;
                    }
                    else
                    {
                        // The two centres around the pixel centre, held inside the outermost ones.
                        var p = Math.Clamp((d + 0.5) * scale - 0.5, 0, sourceLength - 1);
                        var i0 = (int)Math.Floor(p);
                        var w1 = (int)Math.Round((p - i0) * One);

                        first[d] = i0;
                        weights.Add(One - w1);

                        if (i0 + 1 < sourceLength)
                        {
                            weights.Add(w1);
                        }
                    }
                }

                offset[destinationLength] = weights.Count;

                return new Taps(first, offset, weights.ToArray());
            }
        }
    }
}
