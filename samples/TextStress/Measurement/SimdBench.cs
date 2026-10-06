using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;

namespace TextStress.Measurement
{
    /// <summary>
    /// Times the glyph rasterizer and the mask blitter on each SIMD path this runtime supports,
    /// by glyph mask size, so the size below which the scalar path wins can be read per runtime.
    /// Every path must produce the scalar path's bytes; a mismatch fails the run.
    /// </summary>
    /// <remarks>
    /// Masks are real outlines: Latin letters of Inter and CJK ideographs of the system's
    /// fallback face, at pixel sizes from 6 to 96. Each measurement takes the fastest of several
    /// rounds of a fixed amount of work, after a warm-up round.
    /// </remarks>
    internal static class SimdBench
    {
        private static readonly int[] s_sizes = { 6, 8, 10, 12, 14, 16, 20, 24, 32, 48, 64, 96 };

        private const int Rounds = 7;

        public static int Run(RunOptions options)
        {
            var faces = new List<(string Name, GlyphTypeface Face, ushort[] Glyphs)>();
            var inter = new Typeface("fonts:Inter#Inter").GlyphTypeface;

            faces.Add(("latin", inter, GlyphsOf(inter, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")));

            if (FontManager.Current.TryMatchCharacter(0x6C38, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    null, new CultureInfo("zh-Hans"), out var cjk))
            {
                faces.Add(("cjk", cjk.GlyphTypeface, GlyphsOf(cjk.GlyphTypeface, "永字八法国際電腦書體龍鷹體驗韓語漢字繁體簡體")));
            }

            var rows = new List<string>();
            var failures = 0;

            foreach (var (name, face, glyphs) in faces)
            {
                foreach (var size in s_sizes)
                {
                    var masks = BuildMasks(face, glyphs, size);

                    if (masks.Count == 0)
                    {
                        continue;
                    }

                    var cells = 0L;

                    foreach (var mask in masks)
                    {
                        cells += (long)mask.Width * mask.Height;
                    }

                    var meanCells = (double)cells / masks.Count;

                    foreach (var path in new[] { GlyphRasterizerPath.Scalar, GlyphRasterizerPath.Portable,
                                 GlyphRasterizerPath.Vector128, GlyphRasterizerPath.Vector256 })
                    {
                        if (!GlyphRasterizer.IsSupported(path))
                        {
                            continue;
                        }

                        failures += CheckRaster(path, masks);
                        var ns = TimeRaster(path, masks);

                        rows.Add(Row("raster", name, size, path.ToString(), masks.Count, meanCells, ns));
                    }

                    foreach (var path in new[] { GlyphBlitPath.Scalar, GlyphBlitPath.Portable, GlyphBlitPath.AdvSimd,
                                 GlyphBlitPath.Ssse3, GlyphBlitPath.Avx2 })
                    {
                        if (!GlyphMaskBlitter.IsSupported(path))
                        {
                            continue;
                        }

                        failures += CheckBlit(path, masks);
                        var ns = TimeBlit(path, masks);

                        rows.Add(Row("blit", name, size, path.ToString(), masks.Count, meanCells, ns));
                    }

                    StressLog.Info(FormattableString.Invariant($"simd-bench {name} {size}px done"));
                }
            }

            if (options.Out is { } file)
            {
                using var writer = new StreamWriter(file);

                foreach (var (key, value) in RuntimeProbe.Describe())
                {
                    writer.WriteLine("# " + key + "\t" + value);
                }

                foreach (var (key, value) in StressLog.HostEnvironment)
                {
                    writer.WriteLine("# " + key + "\t" + value);
                }

                writer.WriteLine("# tag\t" + options.Tag);
                writer.WriteLine("test\tface\tsize_px\tpath\tmasks\tmean_cells\tns_per_mask\tns_per_cell");

                foreach (var row in rows)
                {
                    writer.WriteLine(row);
                }
            }

            foreach (var row in rows)
            {
                StressLog.Info("simd-bench " + row);
            }

            if (failures > 0)
            {
                StressLog.Error($"simd-bench: {failures} masks differ from the scalar path");
            }

            return failures == 0 ? 0 : 1;
        }

        private static string Row(string test, string face, int size, string path, int masks, double meanCells,
            double ns) =>
            FormattableString.Invariant(
                $"{test}\t{face}\t{size}\t{path}\t{masks}\t{meanCells:F1}\t{ns:F1}\t{ns / meanCells:F3}");

        private static ushort[] GlyphsOf(GlyphTypeface face, string text)
        {
            var glyphs = new List<ushort>();

            foreach (var c in text)
            {
                if (face.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) && glyph != 0)
                {
                    glyphs.Add(glyph);
                }
            }

            return glyphs.ToArray();
        }

        private sealed class Mask
        {
            public required GlyphPathBuilder Path;
            public int Width;
            public int Height;
            public float OffsetX;
            public float OffsetY;
            public byte[] Scalar = Array.Empty<byte>();
            public byte[] Output = Array.Empty<byte>();
        }

        private static List<Mask> BuildMasks(GlyphTypeface face, ushort[] glyphs, int size)
        {
            var scale = (double)size / face.Metrics.DesignEmHeight;
            var design = new Matrix(scale, 0, 0, -scale, 0, 0);
            var masks = new List<Mask>();

            foreach (var glyph in glyphs)
            {
                var builder = new GlyphPathBuilder();

                if (!face.TryBuildGlyphContours(glyph, design, builder) || builder.Points.Length == 0)
                {
                    continue;
                }

                var points = builder.Points;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

                for (var i = 0; i < points.Length; i += 2)
                {
                    minX = Math.Min(minX, points[i]);
                    maxX = Math.Max(maxX, points[i]);
                    minY = Math.Min(minY, points[i + 1]);
                    maxY = Math.Max(maxY, points[i + 1]);
                }

                var left = (int)Math.Floor(minX);
                var top = (int)Math.Floor(minY);
                var mask = new Mask
                {
                    Path = builder,
                    Width = (int)Math.Ceiling(maxX) + 1 - left,
                    Height = (int)Math.Ceiling(maxY) + 1 - top,
                    OffsetX = -left + 0.25f,
                    OffsetY = -top,
                };

                mask.Scalar = new byte[mask.Width * mask.Height];
                mask.Output = new byte[mask.Width * mask.Height];
                GlyphRasterizer.Rasterize(GlyphRasterizerPath.Scalar, builder, mask.Width, mask.Height, mask.OffsetX,
                    mask.OffsetY, false, mask.Scalar, mask.Width);
                masks.Add(mask);
            }

            return masks;
        }

        private static int CheckRaster(GlyphRasterizerPath path, List<Mask> masks)
        {
            var failures = 0;

            foreach (var mask in masks)
            {
                GlyphRasterizer.Rasterize(path, mask.Path, mask.Width, mask.Height, mask.OffsetX, mask.OffsetY, false,
                    mask.Output, mask.Width);

                if (!mask.Output.AsSpan().SequenceEqual(mask.Scalar))
                {
                    failures++;
                }
            }

            return failures;
        }

        private static double TimeRaster(GlyphRasterizerPath path, List<Mask> masks)
        {
            var repeat = Repeat(masks);

            void Round()
            {
                for (var r = 0; r < repeat; r++)
                {
                    foreach (var mask in masks)
                    {
                        GlyphRasterizer.Rasterize(path, mask.Path, mask.Width, mask.Height, mask.OffsetX, mask.OffsetY,
                            false, mask.Output, mask.Width);
                    }
                }
            }

            return Best(Round) / ((double)repeat * masks.Count);
        }

        // The surface the blitter writes, wide and tall enough for the largest mask.
        private const int SurfaceSize = 160;

        private static int CheckBlit(GlyphBlitPath path, List<Mask> masks)
        {
            var expected = Blit(GlyphBlitPath.Scalar, masks);
            var actual = Blit(path, masks);

            return expected.AsSpan().SequenceEqual(actual) ? 0 : 1;
        }

        private static uint[] Blit(GlyphBlitPath path, List<Mask> masks)
        {
            var pixels = new uint[SurfaceSize * SurfaceSize];

            Array.Fill(pixels, 0xFFF4F0E6u);
            BlitAll(path, masks, pixels, 1);

            return pixels;
        }

        private static unsafe void BlitAll(GlyphBlitPath path, List<Mask> masks, uint[] pixels, int repeat)
        {
            var previous = GlyphMaskBlitter.Path;

            GlyphMaskBlitter.Path = path;

            try
            {
                fixed (uint* p = pixels)
                {
                    var target = new GlyphBlitTarget((IntPtr)p, SurfaceSize * 4, SurfaceSize, SurfaceSize,
                        new PixelRect(0, 0, SurfaceSize, SurfaceSize), isRgba: true, GlyphBlitArithmetic.Rounded);

                    for (var r = 0; r < repeat; r++)
                    {
                        for (var i = 0; i < masks.Count; i++)
                        {
                            var mask = masks[i];

                            // A translucent dark tint, so the source table and the destination share
                            // both take part.
                            GlyphMaskBlitter.Blend(target, mask.Scalar, mask.Width, mask.Height, i % 7, i % 5,
                                0xC0202020u, null);
                        }
                    }
                }
            }
            finally
            {
                GlyphMaskBlitter.Path = previous;
            }
        }

        private static double TimeBlit(GlyphBlitPath path, List<Mask> masks)
        {
            var repeat = Repeat(masks);
            var pixels = new uint[SurfaceSize * SurfaceSize];

            return Best(() => BlitAll(path, masks, pixels, repeat)) / ((double)repeat * masks.Count);
        }

        // About two million cells of work per round.
        private static int Repeat(List<Mask> masks)
        {
            var cells = 0L;

            foreach (var mask in masks)
            {
                cells += (long)mask.Width * mask.Height;
            }

            return (int)Math.Clamp(2_000_000 / Math.Max(1, cells), 1, 10000);
        }

        /// <summary>The fastest of the timed rounds after one warm-up round, in nanoseconds.</summary>
        private static double Best(Action round)
        {
            round();

            var best = double.MaxValue;

            for (var i = 0; i < Rounds; i++)
            {
                var start = Stopwatch.GetTimestamp();

                round();

                best = Math.Min(best, (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency);
            }

            return best;
        }
    }
}
