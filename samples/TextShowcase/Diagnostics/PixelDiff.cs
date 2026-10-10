using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TextShowcase.Diagnostics
{
    /// <summary>The comparison of two captures of the same size.</summary>
    internal sealed class DiffResult
    {
        public required WriteableBitmap HeatMap { get; init; }
        public int DifferingPixels { get; init; }
        public int InkPixels { get; init; }
        public int MaxDelta { get; init; }
        public double Rmse { get; init; }
        public int TotalPixels { get; init; }

        public double DifferingPercent => TotalPixels == 0 ? 0 : 100.0 * DifferingPixels / TotalPixels;

        /// <summary>
        /// The verdict on the scale TextLab's A/B view uses, so both tools describe the same
        /// output the same way.
        /// </summary>
        public (string Text, Color Tint) Verdict => DifferingPixels == 0
            ? ("Pixel-identical", Color.FromRgb(0x2E, 0x9E, 0x5B))
            : DifferingPercent < 15 && Rmse < 20
                ? ("Antialiasing-level differences (coverage and gamma), no structural change", Color.FromRgb(0x2E, 0x9E, 0x5B))
                : DifferingPercent < 40
                    ? ("Noticeable differences: check positions", Color.FromRgb(0xE0, 0x8E, 0x0B))
                    : ("Large differences: structural", Color.FromRgb(0xC6, 0x28, 0x28));
    }

    /// <summary>Offscreen captures of a visual and a per-pixel comparison of two of them.</summary>
    internal static class PixelDiff
    {
        /// <summary>
        /// Renders <paramref name="visual"/> at its layout size through the CPU raster pipeline.
        /// Offscreen surfaces never get subpixel text, so captures are grayscale-antialiased.
        /// </summary>
        public static RenderTargetBitmap Capture(Visual visual, double scale = 1)
        {
            var size = visual.Bounds.Size;
            var pixels = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));

            bitmap.Render(visual);

            return bitmap;
        }

        public static byte[] ReadPixels(Bitmap bitmap, out int width, out int height)
        {
            width = bitmap.PixelSize.Width;
            height = bitmap.PixelSize.Height;

            var buffer = new byte[width * height * 4];

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)p, buffer.Length, width * 4);
                }
            }

            return buffer;
        }

        /// <summary>
        /// Compares two BGRA captures over their common area. The heat map shows the shared ink
        /// faintly and paints differing pixels by the size of the largest channel difference.
        /// </summary>
        public static DiffResult Compare(Bitmap a, Bitmap b)
        {
            var pa = ReadPixels(a, out var wa, out var ha);
            var pb = ReadPixels(b, out var wb, out var hb);
            var width = Math.Min(wa, wb);
            var height = Math.Min(ha, hb);
            var heat = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            long squares = 0;
            var differing = 0;
            var ink = 0;
            var max = 0;

            using (var locked = heat.Lock())
            {
                unsafe
                {
                    var dst = (byte*)locked.Address;

                    for (var y = 0; y < height; y++)
                    {
                        var row = dst + y * locked.RowBytes;

                        for (var x = 0; x < width; x++)
                        {
                            var ia = (y * wa + x) * 4;
                            var ib = (y * wb + x) * 4;
                            var db = Math.Abs(pa[ia] - pb[ib]);
                            var dg = Math.Abs(pa[ia + 1] - pb[ib + 1]);
                            var dr = Math.Abs(pa[ia + 2] - pb[ib + 2]);
                            var delta = Math.Max(dr, Math.Max(dg, db));

                            squares += (long)dr * dr + (long)dg * dg + (long)db * db;
                            max = Math.Max(max, delta);

                            var luminance = (pa[ia] + pa[ia + 1] + pa[ia + 2]) / 3;

                            if (luminance < 250)
                            {
                                ink++;
                            }

                            byte r, g, bl;

                            if (delta <= 2)
                            {
                                // Shared ink at a quarter strength, so the text stays readable under the heat.
                                var faint = (byte)(255 - (255 - luminance) / 4);
                                r = g = bl = faint;
                            }
                            else
                            {
                                differing++;
                                (r, g, bl) = delta switch
                                {
                                    <= 8 => ((byte)0xF2, (byte)0xD3, (byte)0x4F),
                                    <= 32 => ((byte)0xF0, (byte)0x8A, (byte)0x24),
                                    _ => ((byte)0xD3, (byte)0x2F, (byte)0x2F),
                                };
                            }

                            var px = row + x * 4;
                            px[0] = bl;
                            px[1] = g;
                            px[2] = r;
                            px[3] = 255;
                        }
                    }
                }
            }

            return new DiffResult
            {
                HeatMap = heat,
                DifferingPixels = differing,
                InkPixels = ink,
                MaxDelta = max,
                Rmse = width * height == 0 ? 0 : Math.Sqrt(squares / (3.0 * width * height)),
                TotalPixels = width * height,
            };
        }
    }
}
