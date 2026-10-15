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
    /// valid and the result does not depend on the platform. Under a rotation, skew or anisotropic
    /// scale, <see cref="Resample"/> maps the bitmap through an affine transform instead.
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

        /// <summary>The most samples a destination pixel takes along one of its axes.</summary>
        private const int MaxSamplesPerAxis = 16;

        /// <summary>
        /// Writes <paramref name="source"/> resampled under an affine map into a
        /// <paramref name="width"/> by <paramref name="height"/> destination, rows <c>width * 4</c>
        /// bytes apart. The destination point (x, y) reads the source point
        /// (<paramref name="m11"/> x + <paramref name="m21"/> y + <paramref name="offsetX"/>,
        /// <paramref name="m12"/> x + <paramref name="m22"/> y + <paramref name="offsetY"/>), in units
        /// where texel i spans [i, i + 1]. Each destination pixel averages bilinear samples spread
        /// evenly over its square, as many along each axis as the source texels that axis of the
        /// pixel covers, so a map that shrinks averages the texels a pixel covers and one that grows
        /// interpolates between the texel centres nearest to the pixel centre. Outside the source
        /// is transparent, so its edges fade over half a texel. Every channel takes the same
        /// weights, so premultiplied texels stay valid.
        /// </summary>
        public static void Resample(in DecodedGlyphBitmap source, float m11, float m12, float m21, float m22,
            float offsetX, float offsetY, int width, int height, Span<byte> destination)
        {
            var columns = Math.Clamp((int)MathF.Round(MathF.Sqrt(m11 * m11 + m12 * m12)), 1, MaxSamplesPerAxis);
            var rows = Math.Clamp((int)MathF.Round(MathF.Sqrt(m21 * m21 + m22 * m22)), 1, MaxSamplesPerAxis);
            var weight = 1f / (columns * rows);
            var bgra = source.Bgra;
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float b = 0, g = 0, r = 0, a = 0;

                    for (var row = 0; row < rows; row++)
                    {
                        var sampleY = y + (row + 0.5f) / rows;

                        for (var column = 0; column < columns; column++)
                        {
                            var sampleX = x + (column + 0.5f) / columns;

                            // Texel centres sit at half units: the texels around the point and the
                            // point's position between their centres.
                            var u = m11 * sampleX + m21 * sampleY + offsetX - 0.5f;
                            var v = m12 * sampleX + m22 * sampleY + offsetY - 0.5f;
                            var u0 = (int)MathF.Floor(u);
                            var v0 = (int)MathF.Floor(v);
                            var fu = u - u0;
                            var fv = v - v0;

                            Accumulate(bgra, sourceWidth, sourceHeight, u0, v0, (1 - fu) * (1 - fv), ref b, ref g, ref r, ref a);
                            Accumulate(bgra, sourceWidth, sourceHeight, u0 + 1, v0, fu * (1 - fv), ref b, ref g, ref r, ref a);
                            Accumulate(bgra, sourceWidth, sourceHeight, u0, v0 + 1, (1 - fu) * fv, ref b, ref g, ref r, ref a);
                            Accumulate(bgra, sourceWidth, sourceHeight, u0 + 1, v0 + 1, fu * fv, ref b, ref g, ref r, ref a);
                        }
                    }

                    var target = destination.Slice((y * width + x) * 4, 4);

                    target[0] = ToLevel(b * weight);
                    target[1] = ToLevel(g * weight);
                    target[2] = ToLevel(r * weight);
                    target[3] = ToLevel(a * weight);
                }
            }
        }

        private static void Accumulate(byte[] bgra, int width, int height, int x, int y, float weight,
            ref float b, ref float g, ref float r, ref float a)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height || weight == 0)
            {
                return;
            }

            var i = (y * width + x) * 4;

            b += weight * bgra[i];
            g += weight * bgra[i + 1];
            r += weight * bgra[i + 2];
            a += weight * bgra[i + 3];
        }

        private static byte ToLevel(float value)
        {
            var level = (int)(value + 0.5f);

            return (byte)(level > 255 ? 255 : level < 0 ? 0 : level);
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
