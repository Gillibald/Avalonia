using System;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>
    /// Deterministic source images. The same seed produces the same pixels on every platform,
    /// so the fixtures encoded from them are byte-identical across runs.
    /// </summary>
    public static class SyntheticImages
    {
        /// <summary>
        /// A photo stand-in in straight-alpha BGRA: multi-octave value noise over broad colour
        /// gradients, plus fine per-pixel grain, so JPEG and PNG see photo-like entropy rather
        /// than flat areas. With <paramref name="alphaRamp"/> the alpha falls from 255 to 32
        /// across the width; otherwise it is opaque.
        /// </summary>
        public static byte[] Photo(int width, int height, bool alphaRamp = false, uint seed = 1)
        {
            var pixels = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var u = (double)x / width;
                    var v = (double)y / height;
                    var detail = Fbm(x / 160.0, y / 160.0, seed);
                    var grain = (int)(Hash((uint)x, (uint)y, seed ^ 0x9E3779B9u) & 15) - 7;
                    var i = (y * width + x) * 4;

                    pixels[i] = Clamp(255 * (0.25 + 0.5 * v) * detail + grain);
                    pixels[i + 1] = Clamp(255 * (0.3 + 0.4 * u) * Fbm(x / 97.0 + 13, y / 97.0, seed + 1) + grain);
                    pixels[i + 2] = Clamp(255 * (0.2 + 0.6 * (1 - u * v)) * Fbm(x / 230.0, y / 230.0 + 7, seed + 2) + grain);
                    pixels[i + 3] = alphaRamp ? (byte)(255 - 223 * x / Math.Max(1, width - 1)) : (byte)255;
                }
            }

            return pixels;
        }

        /// <summary>
        /// A user-interface stand-in as indices into <see cref="UiPalette"/>: flat panels with
        /// borders, separator lines and rows of glyph-like strokes, the content PNG and GIF
        /// screenshots carry.
        /// </summary>
        public static byte[] UiIndices(int width, int height, uint seed = 7)
        {
            var indices = new byte[width * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var panelX = x / 320;
                    var panelY = y / 180;
                    var inPanelX = x % 320;
                    var inPanelY = y % 180;
                    var panel = (byte)(Hash((uint)panelX, (uint)panelY, seed) % 12);
                    byte index;

                    if (inPanelX < 2 || inPanelY < 2)
                        index = 12; // border
                    else if (inPanelY % 24 is >= 8 and < 20 && inPanelX is > 16 and < 300)
                    {
                        // A text row: strokes of glyph width with gaps between words.
                        var glyph = Hash((uint)(x / 7), (uint)(y / 24), seed + 3);
                        var stroke = (glyph >> 4) % 5 != 0 && Hash((uint)x, (uint)y, seed + 5) % 3 != 0;
                        index = stroke ? (byte)(13 + glyph % 3) : panel;
                    }
                    else if (inPanelY == 150)
                        index = 16; // separator
                    else
                        index = panel;

                    indices[y * width + x] = index;
                }
            }

            return indices;
        }

        /// <summary>The 64-colour palette the UI image indexes, as straight-alpha BGRA entries.</summary>
        public static byte[] UiPalette()
        {
            var palette = new byte[64 * 4];

            for (var i = 0; i < 64; i++)
            {
                var h = Hash((uint)i, 99, 11);

                palette[i * 4] = (byte)(40 + h % 200);
                palette[i * 4 + 1] = (byte)(40 + (h >> 8) % 200);
                palette[i * 4 + 2] = (byte)(40 + (h >> 16) % 200);
                palette[i * 4 + 3] = 255;
            }

            return palette;
        }

        /// <summary>Expands indices to straight-alpha BGRA through <paramref name="palette"/>.</summary>
        public static byte[] Expand(byte[] indices, byte[] palette)
        {
            var pixels = new byte[indices.Length * 4];

            for (var i = 0; i < indices.Length; i++)
                Buffer.BlockCopy(palette, indices[i] * 4, pixels, i * 4, 4);

            return pixels;
        }

        /// <summary>Rec. 601 luminance of straight BGRA pixels.</summary>
        public static byte[] Gray(byte[] bgra)
        {
            var gray = new byte[bgra.Length / 4];

            for (var i = 0; i < gray.Length; i++)
                gray[i] = (byte)((bgra[i * 4] * 29 + bgra[i * 4 + 1] * 150 + bgra[i * 4 + 2] * 77) >> 8);

            return gray;
        }

        /// <summary>
        /// Widens straight BGRA to little-endian 16-bit RGBA, with low bytes from the grain so the
        /// extra precision carries information.
        /// </summary>
        public static byte[] Rgba64(byte[] bgra, int width)
        {
            var wide = new byte[bgra.Length * 2];

            for (var i = 0; i < bgra.Length / 4; i++)
            {
                var low = (byte)Hash((uint)(i % width), (uint)(i / width), 17);

                foreach (var (channel, source) in new[] { (0, 2), (1, 1), (2, 0), (3, 3) })
                {
                    wide[i * 8 + channel * 2] = source == 3 ? bgra[i * 4 + 3] : low;
                    wide[i * 8 + channel * 2 + 1] = bgra[i * 4 + source];
                }
            }

            return wide;
        }

        private static double Fbm(double x, double y, uint seed)
        {
            double sum = 0, amplitude = 0.5, total = 0;

            for (var octave = 0; octave < 5; octave++)
            {
                sum += amplitude * ValueNoise(x, y, seed + (uint)octave);
                total += amplitude;
                x *= 2.03;
                y *= 2.03;
                amplitude *= 0.5;
            }

            return 0.35 + 0.65 * sum / total;
        }

        private static double ValueNoise(double x, double y, uint seed)
        {
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var fx = Smooth(x - x0);
            var fy = Smooth(y - y0);

            double Corner(int cx, int cy) => (Hash((uint)cx, (uint)cy, seed) & 0xFFFF) / 65535.0;

            var top = Lerp(Corner(x0, y0), Corner(x0 + 1, y0), fx);
            var bottom = Lerp(Corner(x0, y0 + 1), Corner(x0 + 1, y0 + 1), fx);

            return Lerp(top, bottom, fy);
        }

        private static double Smooth(double t) => t * t * (3 - 2 * t);

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static uint Hash(uint x, uint y, uint seed)
        {
            var h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ seed * 0xCB1AB31Fu;

            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;

            return h;
        }

        private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}
