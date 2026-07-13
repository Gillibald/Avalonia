using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Parity harness for the managed glyph rasterizer: rasterizes a glyph corpus with both the
    /// managed path (table walkers → <see cref="GlyphRasterizer"/>) and Skia (same font bytes,
    /// unhinted, subpixel-positioned) into identical mask rects and reports per-size coverage
    /// deltas. This is a tolerance comparison, not a golden test — the checked-in numbers live in
    /// planning/glyph-rasterizer-parity.md, and the assertions here are a structural gate
    /// (catching flipped axes, misplaced masks, broken winding), deliberately looser than the
    /// typical measured deltas.
    /// </summary>
    public class GlyphRasterizerComparisonTests
    {
        private static readonly float[] s_sizes = { 9f, 12f, 16f, 24f, 48f, 96f };
        private static readonly float[] s_phases = { 0f, 0.25f, 0.5f, 0.75f };
        private const int Apron = 2;

        private readonly ITestOutputHelper _output;

        public GlyphRasterizerComparisonTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Managed_Masks_Track_Skia_Masks_Across_Sizes_And_Phases()
        {
            var report = new List<string>
            {
                "font            size  glyphs  vs-text-mean  vs-path-mean  vs-path-worst",
            };
            var failures = new List<string>();

            foreach (var (fileName, label) in new[]
            {
                ("Inter-Regular.ttf", "Inter (glyf)"),
                ("SourceCodePro-Subset.otf", "SourceCP (CFF)"),
            })
            {
                // Both rasterizers consume the exact same bytes: one SKTypeface feeds Skia's
                // mask directly and Avalonia's GlyphTypeface (table parsing) via SkiaTypeface.
                var bytes = LoadFontBytes(fileName);
                using var skData = SKData.CreateCopy(bytes);
                using var skTypeface = SKTypeface.FromData(skData);
                Assert.NotNull(skTypeface);

                var typeface = new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));

                var glyphs = SelectGlyphs(typeface);
                Assert.True(glyphs.Count >= 3, $"{label}: not enough usable glyphs for a comparison.");

                foreach (var size in s_sizes)
                {
                    var textMeans = new List<double>();
                    var pathMeans = new List<double>();

                    foreach (var glyph in glyphs)
                    {
                        foreach (var phase in s_phases)
                        {
                            if (CompareOne(typeface, skTypeface!, glyph, size, phase) is { } delta)
                            {
                                textMeans.Add(delta.VsText);
                                pathMeans.Add(delta.VsPath);
                            }
                        }
                    }

                    Assert.NotEmpty(pathMeans);

                    var textMean = textMeans.Average();
                    var pathMean = pathMeans.Average();
                    var pathWorst = pathMeans.Max();

                    report.Add(FormattableString.Invariant(
                        $"{label,-15} {size,4:0}px {pathMeans.Count / s_phases.Length,5}   {textMean,10:0.0000}   {pathMean,10:0.0000}   {pathWorst,10:0.0000}"));

                    // Gate on the path-fill comparison — the same outline geometry rasterized by
                    // both engines with no text-specific mask processing on either side. The
                    // vs-text column is informational: it additionally contains whatever contrast
                    // shaping Skia applies to text masks but not to path fills.
                    var (meanGate, worstGate) = size switch
                    {
                        <= 9f => (0.05, 0.10),
                        <= 16f => (0.04, 0.08),
                        _ => (0.03, 0.06),
                    };

                    if (pathMean > meanGate)
                    {
                        failures.Add($"{label} at {size}px: mean vs-path delta {pathMean:0.0000} exceeds {meanGate}");
                    }

                    if (pathWorst > worstGate)
                    {
                        failures.Add($"{label} at {size}px: worst vs-path delta {pathWorst:0.0000} exceeds {worstGate}");
                    }
                }
            }

            foreach (var line in report)
            {
                _output.WriteLine(line);
            }

            // Passing-run output is not shown by the terminal reporter; set this variable to a
            // file path to capture the measured table (the source for refreshing the checked-in
            // summary in planning/glyph-rasterizer-parity.md).
            if (Environment.GetEnvironmentVariable("GLYPH_PARITY_REPORT") is { Length: > 0 } reportPath)
            {
                File.WriteAllLines(reportPath, report);
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            // Walk up from the test output directory to the repo's tests folder — the same
            // convention the render tests use — so the .otf corpus needs no embedding.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }

        private readonly record struct MaskDelta(double VsText, double VsPath);

        /// <summary>
        /// Compares one (glyph, size, phase) pair; returns mean absolute coverage deltas
        /// normalized to [0, 1] — managed vs Skia text draw and managed vs Skia path fill — or
        /// null for a glyph with no ink at this size.
        /// </summary>
        private static MaskDelta? CompareOne(GlyphTypeface typeface, SKTypeface skTypeface,
            ushort glyph, float size, float phase)
        {
            var scale = size / typeface.Metrics.DesignEmHeight;

            if (!typeface.TryGetGlyphInkBounds(glyph, out var box))
            {
                return null;
            }

            var left = box.XMin * scale;
            var right = box.XMax * scale;
            var top = -box.YMax * scale;
            var bottom = -box.YMin * scale;

            var maskLeft = (int)Math.Floor(left) - Apron;
            var maskTop = (int)Math.Floor(top) - Apron;
            var width = (int)Math.Ceiling(right) + Apron - maskLeft;
            var height = (int)Math.Ceiling(bottom) + Apron - maskTop;

            if (width <= Apron * 2 || height <= Apron * 2 || width > 512 || height > 512)
            {
                return null;
            }

            var offsetX = -maskLeft + phase;
            var offsetY = -maskTop;

            // Managed mask.
            var builder = new GlyphPathBuilder();
            var transform = new Avalonia.Matrix(scale, 0, 0, -scale, 0, 0);

            if (!typeface.TryBuildGlyphContours(glyph, transform, builder))
            {
                return null;
            }

            var managed = new byte[width * height];
            GlyphRasterizer.Rasterize(builder, width, height, offsetX, offsetY, false, managed);

            // Two Skia references from the same bytes into the same rect, unhinted and
            // subpixel-positioned: the text pipeline (what DrawGlyphRun produces today) and a
            // plain AA path fill of the glyph outline (pure geometry, no text mask processing).
            var skiaText = RenderSkiaMask(skTypeface, glyph, size, width, height, offsetX, offsetY, asPath: false);
            var skiaPath = RenderSkiaMask(skTypeface, glyph, size, width, height, offsetX, offsetY, asPath: true);

            long sumText = 0;
            long sumPath = 0;

            for (var i = 0; i < managed.Length; i++)
            {
                sumText += Math.Abs(managed[i] - skiaText[i]);
                sumPath += Math.Abs(managed[i] - skiaPath[i]);
            }

            if (managed.Sum(b => (long)b) == 0 && skiaPath.Sum(b => (long)b) == 0)
            {
                return null;
            }

            var pixels = 255.0 * managed.Length;

            return new MaskDelta(sumText / pixels, sumPath / pixels);
        }

        private static byte[] RenderSkiaMask(SKTypeface skTypeface, ushort glyph, float size,
            int width, int height, float offsetX, float offsetY, bool asPath)
        {
            using var font = new SKFont(skTypeface, size)
            {
                Hinting = SKFontHinting.None,
                Subpixel = true,
                Edging = SKFontEdging.Antialias,
                ForceAutoHinting = false,
                BaselineSnap = false,
            };

            var result = new byte[width * height];

            var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info) ?? SKSurface.Create(
                info.WithColorType(SKColorType.Bgra8888));

            Assert.NotNull(surface);

            var canvas = surface!.Canvas;
            canvas.Clear(SKColors.Transparent);

            using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                if (asPath)
                {
                    using var path = font.GetGlyphPath(glyph);

                    if (path is null)
                    {
                        return result;
                    }

                    canvas.Save();
                    canvas.Translate(offsetX, offsetY);
                    canvas.DrawPath(path, paint);
                    canvas.Restore();
                }
                else
                {
                    using var builder = new SKTextBlobBuilder();
                    var run = builder.AllocatePositionedRun(font, 1);
                    run.SetGlyphs(new[] { glyph });
                    run.SetPositions(new[] { new SKPoint(0, 0) });
                    using var blob = builder.Build();

                    if (blob is null)
                    {
                        return result;
                    }

                    canvas.DrawText(blob, offsetX, offsetY, paint);
                }
            }

            using var pixmap = surface.PeekPixels();
            Assert.NotNull(pixmap);

            var pixels = pixmap!.GetPixelSpan();

            if (pixmap.ColorType == SKColorType.Alpha8)
            {
                for (var y = 0; y < height; y++)
                {
                    var row = pixels.Slice(y * pixmap.RowBytes, width);
                    row.CopyTo(result.AsSpan(y * width, width));
                }
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    var row = pixels.Slice(y * pixmap.RowBytes, width * 4);

                    for (var x = 0; x < width; x++)
                    {
                        result[y * width + x] = row[x * 4 + 3];
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Picks printable-ASCII glyphs when the cmap covers them (Inter), otherwise walks raw
        /// glyph ids (the CFF subset fonts map only a handful of characters).
        /// </summary>
        private static List<ushort> SelectGlyphs(GlyphTypeface typeface)
        {
            var glyphs = new List<ushort>();
            var map = typeface.CharacterToGlyphMap;

            foreach (var c in "ABCHORSWaegimoswx038&@")
            {
                if (map.ContainsGlyph(c))
                {
                    glyphs.Add(map[c]);
                }
            }

            if (glyphs.Count < 3)
            {
                for (ushort id = 1; id < typeface.GlyphCount && glyphs.Count < 16; id++)
                {
                    if (typeface.TryGetGlyphInkBounds(id, out var box) && box.XMax > box.XMin && box.YMax > box.YMin)
                    {
                        glyphs.Add(id);
                    }
                }
            }

            return glyphs.Distinct().ToList();
        }
    }
}
