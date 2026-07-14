using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The Slug quality harness: compares the tier's two-ray coverage estimate (via the
    /// reference evaluator, which the GPU shader matches decision-for-decision) against true
    /// area coverage — the analytic cell-coverage rasterizer at 4x supersampling — across a
    /// glyph corpus, a rotation grid, and a zoom ladder rendered from ONE payload per glyph.
    /// This measures the algorithm's intrinsic approximation error, not implementation drift;
    /// the checked-in numbers live in planning/slug-quality.md (SLUG_QUALITY_REPORT captures
    /// the table) and the assertions here are structural gates set above the measured values.
    /// </summary>
    public class SlugQualityOracleTests
    {
        private const string Corpus = "AgoQR&s8";
        private static readonly double[] s_rotations = { 0, 15, 30, 45 };
        private static readonly float[] s_scales = { 6, 12, 24, 48, 120, 300 };

        private readonly ITestOutputHelper _output;

        public SlugQualityOracleTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Two_Ray_Coverage_Tracks_True_Area_Coverage_Across_Rotation_And_Zoom()
        {
            var typeface = LoadTypeface();
            var store = typeface.SlugStore;
            var glyphs = new List<(ushort Id, SlugGlyphPlacement Placement)>();

            foreach (var c in Corpus)
            {
                var id = typeface.CharacterToGlyphMap[c];

                Assert.True(store.TryRealize(typeface, id, out var placement));
                Assert.True(placement.HorizontalBandCount > 0);
                glyphs.Add((id, placement));
            }

            // The tier's core economic property: the whole rotation grid and zoom ladder below
            // renders from the payloads just realized — nothing may be rebuilt per size.
            var versionAfterRealization = store.Version;

            var report = new List<string>
            {
                "rot   scale      mean     worst  miscls        px",
            };
            var failures = new List<string>();

            foreach (var rotation in s_rotations)
            {
                foreach (var scale in s_scales)
                {
                    var mean = 0.0;
                    var worst = 0.0;
                    var misclassified = 0;
                    var pixels = 0L;

                    foreach (var (id, placement) in glyphs)
                    {
                        var cell = MeasureCell(typeface, store, id, in placement, rotation, scale);

                        mean += cell.Sum;
                        worst = Math.Max(worst, cell.Worst);
                        misclassified += cell.Misclassified;
                        pixels += cell.Pixels;
                    }

                    mean /= pixels;

                    report.Add(FormattableString.Invariant(
                        $"{rotation,3:0}° {scale,5:0}px {mean,9:0.00000} {worst,9:0.0000} {misclassified,7} {pixels,9}"));

                    // Structural gates above the measured envelope (mean <= 0.0127, worst
                    // <= 0.38, zero misclassifications across the whole grid — see
                    // planning/slug-quality.md): no pixel may ever cross the misclassification
                    // line, at any size or angle.
                    if (mean > 0.02)
                    {
                        failures.Add($"rot {rotation} scale {scale}: mean {mean:0.00000} exceeds 0.02");
                    }

                    if (worst > 0.45)
                    {
                        failures.Add($"rot {rotation} scale {scale}: worst {worst:0.0000} exceeds 0.45");
                    }

                    if (misclassified > 0)
                    {
                        failures.Add($"rot {rotation} scale {scale}: {misclassified} misclassified pixels");
                    }
                }
            }

            Assert.Equal(versionAfterRealization, store.Version);

            foreach (var line in report)
            {
                _output.WriteLine(line);
            }

            // Passing-run output is not shown by the terminal reporter; set this variable to a
            // file path to capture the measured table (the source for refreshing the checked-in
            // summary in planning/slug-quality.md).
            if (Environment.GetEnvironmentVariable("SLUG_QUALITY_REPORT") is { Length: > 0 } reportPath)
            {
                File.WriteAllLines(reportPath, report);
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private readonly record struct CellResult(double Sum, double Worst, int Misclassified, int Pixels);

        private static CellResult MeasureCell(GlyphTypeface typeface, SlugTexelStore store,
            ushort glyph, in SlugGlyphPlacement placement, double rotationDegrees, float scale)
        {
            // Em → device for this cell, fitted into a margin window like a real draw.
            var matrix = SKMatrix.CreateScale(scale, -scale);

            if (rotationDegrees != 0)
            {
                matrix = SKMatrix.Concat(SKMatrix.CreateRotationDegrees((float)rotationDegrees), matrix);
            }

            Span<SKPoint> corners = stackalloc SKPoint[]
            {
                matrix.MapPoint(new SKPoint(placement.MinX, placement.MinY)),
                matrix.MapPoint(new SKPoint(placement.MaxX, placement.MinY)),
                matrix.MapPoint(new SKPoint(placement.MinX, placement.MaxY)),
                matrix.MapPoint(new SKPoint(placement.MaxX, placement.MaxY)),
            };

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

            foreach (var corner in corners)
            {
                minX = Math.Min(minX, corner.X);
                minY = Math.Min(minY, corner.Y);
                maxX = Math.Max(maxX, corner.X);
                maxY = Math.Max(maxY, corner.Y);
            }

            const int margin = 3;

            var width = (int)Math.Ceiling(maxX - minX) + margin * 2;
            var height = (int)Math.Ceiling(maxY - minY) + margin * 2;
            var emToDevice = SKMatrix.Concat(SKMatrix.CreateTranslation(margin - minX, margin - minY), matrix);

            Assert.True(emToDevice.TryInvert(out var deviceToEm));

            var emsPerPixelX = Math.Abs(deviceToEm.ScaleX) + Math.Abs(deviceToEm.SkewX);
            var emsPerPixelY = Math.Abs(deviceToEm.SkewY) + Math.Abs(deviceToEm.ScaleY);

            // Oracle: the same quadratic chains, transformed and rasterized at 4x, then
            // box-averaged to true per-pixel area coverage.
            const int super = 4;

            var data = typeface.SlugCache.GetOrBuild<object?>(glyph, null,
                static (_, _) => null);

            Assert.NotNull(data);

            var builder = new GlyphPathBuilder();

            builder.SetFillRule(data!.FillRule);

            for (var contour = 0; contour < data.ContourCount; contour++)
            {
                var start = data.GetContourStart(contour);
                var count = data.GetContourCurveCount(contour);
                var first = data.GetCurve(start);

                builder.BeginFigure(MapSuper(first.X1, first.Y1));

                for (var j = 0; j < count; j++)
                {
                    var curve = data.GetCurve(start + j);

                    builder.QuadraticBezierTo(MapSuper(curve.X2, curve.Y2), MapSuper(curve.X3, curve.Y3));
                }

                builder.EndFigure(true);
            }

            var superMask = new byte[width * super * height * super];

            GlyphRasterizer.Rasterize(builder, width * super, height * super, 0, 0, aliased: false, superMask);

            var sum = 0.0;
            var worstDelta = 0.0;
            var misclassified = 0;

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var em = deviceToEm.MapPoint(new SKPoint(px + 0.5f, py + 0.5f));

                    var slug = SlugReferenceEvaluator.Evaluate(
                        store.CurveTexels, store.BandTexels, in placement,
                        em.X, em.Y, emsPerPixelX, emsPerPixelY);

                    var area = 0;

                    for (var sy = 0; sy < super; sy++)
                    {
                        var row = ((py * super + sy) * width + px) * super;

                        for (var sx = 0; sx < super; sx++)
                        {
                            area += superMask[row + sx];
                        }
                    }

                    var delta = Math.Abs(slug - area / (255.0 * super * super));

                    sum += delta;
                    worstDelta = Math.Max(worstDelta, delta);

                    if (delta > 0.5)
                    {
                        misclassified++;
                    }
                }
            }

            return new CellResult(sum, worstDelta, misclassified, width * height);

            Point MapSuper(float emX, float emY)
            {
                var device = emToDevice.MapPoint(new SKPoint(emX, emY));

                return new Point(device.X * super, device.Y * super);
            }
        }

        private static GlyphTypeface LoadTypeface()
        {
            var bytes = LoadFontBytes("Inter-Regular.ttf");
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));
        }

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
