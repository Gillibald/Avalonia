using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.Media.TextFormatting;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// The rendering core of the pipeline inspector: each method draws one stage of the
    /// managed glyph pipeline (hinting warps, mask anatomy, the ClearType stages, the Slug
    /// payload) into an <see cref="SKBitmap"/>. The interactive inspector page shows these
    /// live; <see cref="ExportAll"/> writes the deterministic Inter figures embedded in
    /// docs/glyph-rasterization/.
    /// </summary>
    internal static class PipelineFigures
    {
        /// <summary>Figure labels render invariant — a de-DE machine must not draw "6,5".</summary>
        private static string Inv(FormattableString text) => FormattableString.Invariant(text);

        private static readonly SKColor s_grid = new(0xE6, 0xE6, 0xE6);
        private static readonly SKColor s_unhinted = new(0xD4, 0x33, 0x22);
        private static readonly SKColor s_hinted = new(0x22, 0x44, 0xCC);
        private static readonly SKColor s_zone = new(0x22, 0x99, 0x33);
        private static readonly SKColor s_stroke = new(0xEE, 0x88, 0x00);

        /// <summary>
        /// One glyph on the pixel grid: the rasterized mask, the outline before (red) and
        /// after (blue) the hinting warps, the zone rows (green, with their pre-snap source
        /// positions dashed) and the per-glyph stroke pairs (orange).
        /// </summary>
        public static SKBitmap HintingAnatomy(GlyphTypeface typeface, ushort glyph, string label, float size,
            TextHintingMode hinting)
        {
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);
            var gridFit = hinting != TextHintingMode.None;
            var stemSnap = hinting == TextHintingMode.Strong;

            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(),
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, gridFit, stemSnap));

            var unhinted = new GlyphPathBuilder();
            typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), unhinted);

            var hinted = new GlyphPathBuilder();
            typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), hinted);

            var zones = typeface.GridFit.GetWarp(scaleQ);
            Span<float> strokeFrom = stackalloc float[16];
            Span<float> strokeTo = stackalloc float[16];
            var strokeKnots = 0;

            if (gridFit)
            {
                var designToPixels = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);

                strokeKnots = StemFit.CollectStrokeKnots(unhinted, zones.From, 0.75f,
                    typeface.StemWidths.HorizontalStrokeWidths, designToPixels, strokeFrom, strokeTo);
                hinted.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(hinted, scaleQ,
                    typeface.StemWidths.HorizontalStrokeWidths));
            }

            if (stemSnap)
            {
                hinted.ApplyHorizontalWarp(StemFit.BuildWarp(hinted, 1f,
                    typeface.StemWidths.VerticalStemWidths, scale));
            }

            var zoom = Math.Clamp(400 / Math.Max(mask.Width, Math.Max(mask.Height, 1)), 6, 30);
            const int marginLeft = 96;
            const int marginTop = 28;
            var width = marginLeft + mask.Width * zoom + 150;
            var height = marginTop + mask.Height * zoom + 76;

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            // Mask cells and the pixel grid.
            using (var cell = new SKPaint())
            using (var grid = new SKPaint { Color = s_grid, IsStroke = true })
            {
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var coverage = mask.IsEmpty ? (byte)0 : mask.Alpha[y * mask.Width + x];

                        if (coverage > 0)
                        {
                            cell.Color = new SKColor(0x30, 0x30, 0x30, coverage);
                            canvas.DrawRect(marginLeft + x * zoom, marginTop + y * zoom, zoom, zoom, cell);
                        }
                    }
                }

                for (var x = 0; x <= mask.Width; x++)
                {
                    canvas.DrawLine(marginLeft + x * zoom, marginTop, marginLeft + x * zoom,
                        marginTop + mask.Height * zoom, grid);
                }

                for (var y = 0; y <= mask.Height; y++)
                {
                    canvas.DrawLine(marginLeft, marginTop + y * zoom, marginLeft + mask.Width * zoom,
                        marginTop + y * zoom, grid);
                }
            }

            float DeviceX(float x) => marginLeft + (x - mask.Left) * zoom;
            float DeviceY(float y) => marginTop + (y - mask.Top) * zoom;

            // Zone rows: solid green at the landed row, dashed at the pre-snap source.
            if (gridFit && !zones.IsIdentity)
            {
                using var zonePaint = new SKPaint { Color = s_zone, IsStroke = true, StrokeWidth = 2 };
                using var sourcePaint = new SKPaint
                {
                    Color = s_zone.WithAlpha(0x90), IsStroke = true,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0),
                };
                using var font = new SKFont(SKTypeface.Default, 12);
                using var text = new SKPaint { Color = s_zone };

                for (var i = 0; i < zones.From.Length; i++)
                {
                    if (zones.To[i] < mask.Top || zones.To[i] > mask.Top + mask.Height)
                    {
                        continue;   // a font-wide zone this glyph never reaches
                    }

                    var destination = DeviceY(zones.To[i]);

                    canvas.DrawLine(marginLeft - 44, destination, marginLeft + mask.Width * zoom, destination, zonePaint);
                    canvas.DrawLine(marginLeft, DeviceY(zones.From[i]), marginLeft + mask.Width * zoom,
                        DeviceY(zones.From[i]), sourcePaint);

                    // Band-edge knots share their zone's row; label each landed row once.
                    if (i == 0 || zones.To[i] != zones.To[i - 1])
                    {
                        canvas.DrawText(Inv($"{zones.From[i]:0.##}→{zones.To[i]:0}"), 4, destination + 4,
                            SKTextAlign.Left, font, text);
                    }
                }
            }

            // Per-glyph stroke pairs.
            if (strokeKnots > 0)
            {
                using var strokePaint = new SKPaint { Color = s_stroke, IsStroke = true, StrokeWidth = 2 };
                using var font = new SKFont(SKTypeface.Default, 12);
                using var text = new SKPaint { Color = s_stroke };
                var right = marginLeft + mask.Width * zoom;

                for (var i = 0; i < strokeKnots; i++)
                {
                    canvas.DrawLine(right, DeviceY(strokeTo[i]), right + 26, DeviceY(strokeTo[i]), strokePaint);
                    canvas.DrawText(Inv($"{strokeFrom[i]:0.##}→{strokeTo[i]:0}"), right + 30,
                        DeviceY(strokeTo[i]) + 4, SKTextAlign.Left, font, text);
                }
            }

            DrawOutline(canvas, unhinted, DeviceX, DeviceY, s_unhinted);
            DrawOutline(canvas, hinted, DeviceX, DeviceY, s_hinted);

            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var text = new SKPaint { Color = SKColors.Black })
            {
                canvas.DrawText(
                    Inv($"{label} {size:0.#}px  hinting {hinting}  |  red unhinted, blue grid-fit, green zones (dashed = source), orange stroke pairs"),
                    6, height - 10, SKTextAlign.Left, font, text);
            }

            return bitmap;
        }

        /// <summary>
        /// Mask anatomy: the focus glyph's mask magnified with its apron marked, the cache key
        /// fields, and a run composed from per-glyph masks shown at 1x and 4x.
        /// </summary>
        public static SKBitmap MaskAnatomy(GlyphTypeface typeface, ushort glyph, string label, string runText, float size)
        {
            // Compact enough for a quadrant; the run redraws at 3x instead of 4x.
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);
            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(),
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased));

            const int zoom = 11;
            var maskPanelWidth = Math.Max(mask.Width * zoom + 20, 300);

            // Compose the run into a BGRA buffer exactly the way the renderer does.
            var runWidth = 8;
            var scratch = new GlyphPathBuilder();

            foreach (var c in runText)
            {
                typeface.TryGetGlyphMetrics(typeface.CharacterToGlyphMap[c], out var metrics);
                runWidth += (int)Math.Ceiling(metrics.AdvanceWidth * scale);
            }

            var runHeight = (int)Math.Ceiling(size * 1.7);
            var baseline = (int)Math.Ceiling(size * 1.25);
            var run = new byte[runWidth * runHeight * 4];
            var penX = 4f;

            foreach (var c in runText)
            {
                var g = typeface.CharacterToGlyphMap[c];
                var glyphMask = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(g, scaleQ, 0, GlyphMaskMode.Antialiased));

                RunMaskComposer.ComposeTinted(glyphMask, (int)Math.Round(penX), baseline,
                    RunMaskComposer.MakeTint(255, 0, 0, 0), run, runWidth, runHeight);

                typeface.TryGetGlyphMetrics(g, out var metrics);
                penX += metrics.AdvanceWidth * scale;
            }

            var width = Math.Max(Math.Max(maskPanelWidth + 480, runWidth * 3 + 40), 600);
            var height = Math.Max(mask.Height * zoom, 126) + runHeight * 4 + 104;
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            // Focus mask with apron.
            using (var cell = new SKPaint())
            using (var grid = new SKPaint { Color = s_grid, IsStroke = true })
            using (var apron = new SKPaint
                   {
                       Color = s_stroke, IsStroke = true, StrokeWidth = 2,
                       PathEffect = SKPathEffect.CreateDash(new float[] { 5, 3 }, 0),
                   })
            {
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var coverage = mask.IsEmpty ? (byte)0 : mask.Alpha[y * mask.Width + x];

                        if (coverage > 0)
                        {
                            cell.Color = new SKColor(0x30, 0x30, 0x30, coverage);
                            canvas.DrawRect(10 + x * zoom, 28 + y * zoom, zoom, zoom, cell);
                        }
                    }
                }

                canvas.DrawRect(10, 28, mask.Width * zoom, mask.Height * zoom, grid);
                canvas.DrawRect(10 + GlyphMasks.Apron * zoom, 28 + GlyphMasks.Apron * zoom,
                    (mask.Width - 2 * GlyphMasks.Apron) * zoom, (mask.Height - 2 * GlyphMasks.Apron) * zoom, apron);
            }

            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var text = new SKPaint { Color = SKColors.Black })
            {
                var infoX = Math.Max(10 + mask.Width * zoom + 24, 180);

                canvas.DrawText($"{label} mask, dashed = {GlyphMasks.Apron}px apron", 10, 18, SKTextAlign.Left, font, text);
                canvas.DrawText(Inv($"GlyphMaskKey: glyph {glyph}, scaleQ {scaleQ} ({scaleQ / GlyphMaskKey.ScaleQuantum:0.###} px/em)"), infoX, 46, SKTextAlign.Left, font, text);
                canvas.DrawText($"phase 0 of {GlyphMaskKey.PhaseCount}, mode Antialiased, GridFit, no StemSnap", infoX, 66, SKTextAlign.Left, font, text);
                canvas.DrawText($"mask {mask.Width}x{mask.Height} at ({mask.Left},{mask.Top}), pen-relative", infoX, 86, SKTextAlign.Left, font, text);
                canvas.DrawText($"cache: {typeface.MaskCache.Count} masks resident, budget {GlyphMaskCache.DefaultBudgetBytes / (1024 * 1024)} MB", infoX, 106, SKTextAlign.Left, font, text);
            }

            // The composed run at 1x and 4x.
            var runTop = Math.Max(mask.Height * zoom, 126) + 52;

            using (var image = SKImage.FromPixelCopy(
                       new SKImageInfo(runWidth, runHeight, SKColorType.Bgra8888, SKAlphaType.Premul), run))
            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var text = new SKPaint { Color = SKColors.Black })
            {
                canvas.DrawText(Inv($"run mask composed from per-glyph masks at integer pens (\"{runText}\" {size:0.#}px), 1x and 4x:"),
                    10, runTop - 8, SKTextAlign.Left, font, text);
                canvas.DrawImage(image, new SKRect(10, runTop, 10 + runWidth, runTop + runHeight));
                canvas.DrawImage(image,
                    new SKRect(10, runTop + runHeight + 8, 10 + runWidth * 3, runTop + runHeight + 8 + runHeight * 3),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }

            return bitmap;
        }

        /// <summary>
        /// The ClearType stages side by side: the 3x horizontal analytic raster, the three
        /// FIR-filtered stripe channels, and the final composite on white (optionally without
        /// gamma correction, optionally BGR stripe order).
        /// </summary>
        public static SKBitmap ClearTypePipeline(GlyphTypeface typeface, ushort glyph, string label, float size,
            bool bgr, bool gamma, TextHintingMode hinting)
        {
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);
            var gridFit = hinting != TextHintingMode.None;
            var stemSnap = hinting == TextHintingMode.Strong;

            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(),
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Subpixel, gridFit, stemSnap));

            if (mask.IsEmpty)
            {
                return new SKBitmap(new SKImageInfo(320, 60));
            }

            // Reproduce the 3x raster the way GlyphMasks does, warps included.
            var contours = new GlyphPathBuilder();

            typeface.TryBuildGlyphContours(glyph, new Matrix(scale * 3, 0, 0, -scale, 0, 0), contours);

            if (gridFit)
            {
                contours.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(contours, scaleQ,
                    typeface.StemWidths.HorizontalStrokeWidths));
            }

            if (stemSnap)
            {
                contours.ApplyHorizontalWarp(StemFit.BuildWarp(contours, 3f,
                    typeface.StemWidths.VerticalStemWidths, scale));
            }

            var raw = new byte[mask.Width * 3 * mask.Height];

            GlyphRasterizer.Rasterize(contours, mask.Width * 3, mask.Height, -mask.Left * 3, -mask.Top, false, raw);

            const int zoom = 9;
            const int gap = 20;
            var panelWidth = mask.Width * zoom;
            var raw3Width = mask.Width * 3 * (zoom / 3);
            var width = raw3Width + (panelWidth + gap) * 4 + gap + 20;
            var height = mask.Height * zoom + 74;
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using var cell = new SKPaint();
            using var font = new SKFont(SKTypeface.Default, 13);
            using var text = new SKPaint { Color = SKColors.Black };

            // Panel 1: raw 3x coverage.
            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width * 3; x++)
                {
                    var coverage = raw[y * mask.Width * 3 + x];

                    if (coverage > 0)
                    {
                        cell.Color = new SKColor(0x30, 0x30, 0x30, coverage);
                        canvas.DrawRect(10 + x * (zoom / 3f), 28 + y * zoom, zoom / 3f, zoom, cell);
                    }
                }
            }

            canvas.DrawText("3x analytic raster", 10, 18, SKTextAlign.Left, font, text);

            // Panels 2-4: the FIR-filtered stripe channels from the actual mask.
            var channelColors = new[] { new SKColor(0xCC, 0x22, 0x22), new SKColor(0x22, 0x99, 0x22), new SKColor(0x22, 0x44, 0xCC) };
            var channelNames = new[] { "R stripe", "G stripe", "B stripe" };

            for (var channel = 0; channel < 3; channel++)
            {
                var panelX = 10 + raw3Width + gap + (panelWidth + gap) * channel;

                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var coverage = mask.Alpha[(y * mask.Width + x) * 3 + channel];

                        if (coverage > 0)
                        {
                            cell.Color = channelColors[channel].WithAlpha(coverage);
                            canvas.DrawRect(panelX + x * zoom, 28 + y * zoom, zoom, zoom, cell);
                        }
                    }
                }

                canvas.DrawText($"{channelNames[channel]} (5-tap FIR)", panelX, 18, SKTextAlign.Left, font, text);
            }

            // Panel 5: composite black-on-white, per channel: 255 - g(coverage).
            var table = gamma ? MaskGamma.GetLcdTable(0, 0, 0) : null;
            var compositeX = 10 + raw3Width + gap + (panelWidth + gap) * 3;

            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width; x++)
                {
                    var r = mask.Alpha[(y * mask.Width + x) * 3 + (bgr ? 2 : 0)];
                    var g = mask.Alpha[(y * mask.Width + x) * 3 + 1];
                    var b = mask.Alpha[(y * mask.Width + x) * 3 + (bgr ? 0 : 2)];

                    if (table is not null)
                    {
                        r = table[r];
                        g = table[g];
                        b = table[b];
                    }

                    if (r > 0 || g > 0 || b > 0)
                    {
                        cell.Color = new SKColor((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
                        canvas.DrawRect(compositeX + x * zoom, 28 + y * zoom, zoom, zoom, cell);
                    }
                }
            }

            canvas.DrawText($"composite ({(bgr ? "BGR" : "RGB")}, gamma {(gamma ? "on" : "off")})",
                compositeX, 18, SKTextAlign.Left, font, text);
            canvas.DrawText(
                Inv($"{label} {size:0.#}px  |  3x raster → (1,2,3,2,1)/9 FIR per stripe → interleaved RGB mask → per-channel blend"),
                10, height - 10, SKTextAlign.Left, font, text);

            return bitmap;
        }

        /// <summary>
        /// The Slug payload for one glyph: em-space quadratic chains with the horizontal and
        /// vertical band partition the shader walks, plus the payload statistics.
        /// </summary>
        public static SKBitmap SlugBands(GlyphTypeface typeface, ushort glyph, string label)
        {
            var sink = new SlugContourSink();

            sink.Reset();

            var scale = 1.0 / typeface.Metrics.DesignEmHeight;
            var data = typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, scale, 0, 0), sink)
                ? SlugBandEncoder.Encode(sink)
                : null;

            var bitmap = new SKBitmap(new SKImageInfo(560, 520, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);
            using var font = new SKFont(SKTypeface.Default, 13);
            using var text = new SKPaint { Color = SKColors.Black };

            canvas.Clear(SKColors.White);

            if (data is null)
            {
                canvas.DrawText($"{label}: no Slug payload (no contours or caps exceeded — the tier declines)",
                    10, 30, SKTextAlign.Left, font, text);
                return bitmap;
            }

            const float left = 46;
            const float top = 32;
            var extent = Math.Max(data.MaxX - data.MinX, data.MaxY - data.MinY);
            var s = 370f / extent;

            float MapX(float x) => left + (x - data.MinX) * s;
            float MapY(float y) => top + (data.MaxY - y) * s;   // em space is y-up

            using (var bandPaint = new SKPaint { Color = s_zone.WithAlpha(0x60), IsStroke = true })
            using (var bandText = new SKPaint { Color = s_zone })
            using (var small = new SKFont(SKTypeface.Default, 11))
            {
                for (var i = 0; i <= data.HorizontalBandCount; i++)
                {
                    var y = data.MinY + (data.MaxY - data.MinY) * i / data.HorizontalBandCount;

                    canvas.DrawLine(MapX(data.MinX), MapY(y), MapX(data.MaxX), MapY(y), bandPaint);

                    if (i < data.HorizontalBandCount)
                    {
                        var mid = data.MinY + (data.MaxY - data.MinY) * (i + 0.5f) / data.HorizontalBandCount;

                        canvas.DrawText($"{data.GetHorizontalBand(i).Length}", MapX(data.MaxX) + 6, MapY(mid) + 4,
                            SKTextAlign.Left, small, bandText);
                    }
                }

                for (var i = 0; i <= data.VerticalBandCount; i++)
                {
                    var x = data.MinX + (data.MaxX - data.MinX) * i / data.VerticalBandCount;

                    canvas.DrawLine(MapX(x), MapY(data.MinY), MapX(x), MapY(data.MaxY), bandPaint);

                    if (i < data.VerticalBandCount)
                    {
                        var mid = data.MinX + (data.MaxX - data.MinX) * (i + 0.5f) / data.VerticalBandCount;

                        canvas.DrawText($"{data.GetVerticalBand(i).Length}", MapX(mid) - 4, MapY(data.MinY) + 16,
                            SKTextAlign.Left, small, bandText);
                    }
                }
            }

            using (var curvePaint = new SKPaint
                   {
                       Color = new SKColor(0x20, 0x20, 0x20), IsStroke = true, StrokeWidth = 2, IsAntialias = true,
                   })
            using (var path = new SKPath())
            {
                for (var i = 0; i < data.TotalCurveCount; i++)
                {
                    var curve = data.GetCurve(i);

                    path.MoveTo(MapX(curve.X1), MapY(curve.Y1));
                    path.QuadTo(MapX(curve.X2), MapY(curve.Y2), MapX(curve.X3), MapY(curve.Y3));
                }

                canvas.DrawPath(path, curvePaint);
            }

            var worstBand = 0;

            for (var i = 0; i < data.HorizontalBandCount; i++)
            {
                worstBand = Math.Max(worstBand, data.GetHorizontalBand(i).Length);
            }

            for (var i = 0; i < data.VerticalBandCount; i++)
            {
                worstBand = Math.Max(worstBand, data.GetVerticalBand(i).Length);
            }

            canvas.DrawText(
                $"{label}: {data.ContourCount} contours, {data.TotalCurveCount} quadratic curves, " +
                $"{data.HorizontalBandCount}x{data.VerticalBandCount} bands (worst list {worstBand} of {SlugTexelSerializer.MaxBandListLength}), " +
                $"payload {data.RetainedBytes} B, {data.FillRule}",
                10, 520 - 40, SKTextAlign.Left, font, text);
            canvas.DrawText(
                "green counts = curves per band list; encoded once per glyph ever, in em space",
                10, 520 - 20, SKTextAlign.Left, font, text);

            return bitmap;
        }

        /// <summary>
        /// The repo's Inter asset as a managed typeface — the deterministic figure font,
        /// independent of what the font manager resolves on this machine.
        /// </summary>
        public static GlyphTypeface? LoadRepoInter()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Avalonia.slnx")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                return null;
            }

            var path = Path.Combine(directory.FullName, "tests", "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf");

            if (!File.Exists(path))
            {
                return null;
            }

            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(File.ReadAllBytes(path)));

            return skTypeface is null
                ? null
                : new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));
        }

        /// <summary>
        /// The hinting/rendering-mode waterfall: one size ladder down, one column per hinting
        /// mode, every cell drawn through the REAL pipeline (DrawingContextImpl on an
        /// RGB-striped raster surface, so SubpixelAntialias produces true LCD output).
        /// </summary>
        public static SKBitmap WaterfallMatrix(GlyphTypeface typeface, string text,
            TextRenderingMode rendering)
        {
            float[] sizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24 };

            // Unspecified sits last: the explicit ladder first, then the policy column showing
            // what apps that set nothing actually get (the gasp escalation resolves per size,
            // so a legacy font can be Light at one row and Strong at the next).
            TextHintingMode[] hintings =
            {
                TextHintingMode.None, TextHintingMode.Light, TextHintingMode.Strong,
                TextHintingMode.Unspecified,
            };

            // Column width from the widest (largest) run.
            var scale = sizes[^1] / typeface.Metrics.DesignEmHeight;
            var advance = 0f;

            foreach (var c in text)
            {
                if (typeface.CharacterToGlyphMap.ContainsGlyph(c))
                {
                    typeface.TryGetGlyphMetrics(typeface.CharacterToGlyphMap[c], out var metrics);
                    advance += metrics.AdvanceWidth * scale;
                }
            }

            var columnWidth = (int)Math.Ceiling(advance) + 30;
            const int labelWidth = 44;
            var rowHeights = new int[sizes.Length];
            var height = 26;

            for (var i = 0; i < sizes.Length; i++)
            {
                rowHeights[i] = (int)Math.Ceiling(sizes[i] * 1.6) + 8;
                height += rowHeights[i];
            }

            var info = new SKImageInfo(labelWidth + columnWidth * hintings.Length, height,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));

            surface.Canvas.Clear(SKColors.White);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                for (var column = 0; column < hintings.Length; column++)
                {
                    context.PushTextOptions(new TextOptions
                    {
                        TextRenderingMode = rendering,
                        TextHintingMode = hintings[column],
                    });

                    var y = 26f;

                    for (var row = 0; row < sizes.Length; row++)
                    {
                        // A fractional origin, so Light shows its subpixel positioning.
                        using var run = CreateRun(typeface, text, sizes[row],
                            new Point(labelWidth + columnWidth * column + 8.3, y + sizes[row] * 1.15));

                        context.DrawGlyphRun(Brushes.Black, run);
                        y += rowHeights[row];
                    }

                    context.PopTextOptions();
                }
            }

            using (var font = new SKFont(SKTypeface.Default, 12))
            using (var label = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60) })
            {
                for (var column = 0; column < hintings.Length; column++)
                {
                    surface.Canvas.DrawText($"{hintings[column]}", labelWidth + columnWidth * column + 8, 16,
                        SKTextAlign.Left, font, label);
                }

                var y = 26f;

                for (var row = 0; row < sizes.Length; row++)
                {
                    surface.Canvas.DrawText(Inv($"{sizes[row]:0}"), 8, y + sizes[row] * 1.15f,
                        SKTextAlign.Left, font, label);
                    y += rowHeights[row];
                }
            }

            using var snapshot = surface.Snapshot();
            var bitmap = new SKBitmap(info);

            snapshot.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            return bitmap;
        }

        /// <summary>
        /// Renders the sample subpixel on an RGB-striped surface and classifies every fringed
        /// pixel (|R-B| beyond a threshold) by polarity: warm on a left edge and cool on a
        /// right edge is physically correct for RGB stripes; anything else is flagged. The
        /// returned strip stacks the rendering over the classification map, and the counters
        /// come back for the caller's stats line.
        /// </summary>
        public static SKBitmap FringeAnalysis(GlyphTypeface typeface, string text, float size,
            TextHintingMode hinting, int zoom,
            out int fringed, out int warmLeft, out int coolRight, out int wrongPolarity, out int inkPixels)
        {
            var scale = size / typeface.Metrics.DesignEmHeight;
            var advance = 0f;

            foreach (var c in text)
            {
                if (typeface.CharacterToGlyphMap.ContainsGlyph(c))
                {
                    typeface.TryGetGlyphMetrics(typeface.CharacterToGlyphMap[c], out var metrics);
                    advance += metrics.AdvanceWidth * scale;
                }
            }

            var width = (int)Math.Ceiling(advance) + 20;
            var height = (int)Math.Ceiling(size * 1.7) + 8;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));

            surface.Canvas.Clear(SKColors.White);

            using (var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Canvas = surface.Canvas,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                context.PushTextOptions(new TextOptions
                {
                    TextRenderingMode = TextRenderingMode.SubpixelAntialias,
                    TextHintingMode = hinting,
                });

                using var run = CreateRun(typeface, text, size, new Point(10.3, size * 1.25));

                context.DrawGlyphRun(Brushes.Black, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            using var render = new SKBitmap(info);

            snapshot.ReadPixels(info, render.GetPixels(), render.RowBytes, 0, 0);

            fringed = 0;
            warmLeft = 0;
            coolRight = 0;
            wrongPolarity = 0;
            inkPixels = 0;

            using var map = new SKBitmap(info);

            static int Luma(SKColor c) => 54 * c.Red + 183 * c.Green + 19 * c.Blue;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = render.GetPixel(x, y);

                    if (Luma(pixel) < 240 * 256)
                    {
                        inkPixels++;
                    }

                    if (x == 0 || x == width - 1 || Math.Abs(pixel.Red - pixel.Blue) <= 8)
                    {
                        map.SetPixel(x, y, SKColors.White);
                        continue;
                    }

                    fringed++;

                    var leftInk = Luma(render.GetPixel(x - 1, y)) < Luma(pixel);
                    var rightInk = Luma(render.GetPixel(x + 1, y)) < Luma(pixel);

                    if (pixel.Red > pixel.Blue && rightInk && !leftInk)
                    {
                        warmLeft++;
                        map.SetPixel(x, y, new SKColor(0xE0, 0x50, 0x30));
                    }
                    else if (pixel.Blue > pixel.Red && leftInk && !rightInk)
                    {
                        coolRight++;
                        map.SetPixel(x, y, new SKColor(0x30, 0x60, 0xE0));
                    }
                    else if ((pixel.Red > pixel.Blue && leftInk && !rightInk) ||
                             (pixel.Blue > pixel.Red && rightInk && !leftInk))
                    {
                        // Warm on a right edge or cool on a left edge — swapped stripe order
                        // or a double-blend; this is the defect signal.
                        wrongPolarity++;
                        map.SetPixel(x, y, new SKColor(0xE0, 0x00, 0xE0));
                    }
                    else
                    {
                        // Fringed interior pixel (both or neither neighbor inked) — normal
                        // between tightly spaced stems.
                        map.SetPixel(x, y, new SKColor(0xF0, 0xC0, 0x60));
                    }
                }
            }

            var composed = new SKBitmap(new SKImageInfo(width * zoom,
                (height * zoom + 20) * 2, SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(composed))
            using (var font = new SKFont(SKTypeface.Default, 12))
            using (var label = new SKPaint { Color = SKColors.Black })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText("rendering", 0, 14, SKTextAlign.Left, font, label);
                canvas.DrawText("fringe map: red = warm left edge, blue = cool right edge, magenta = WRONG polarity, amber = interior",
                    0, height * zoom + 34, SKTextAlign.Left, font, label);

                using var renderImage = SKImage.FromBitmap(render);
                using var mapImage = SKImage.FromBitmap(map);

                canvas.DrawImage(renderImage, new SKRect(0, 20, width * zoom, 20 + height * zoom),
                    new SKSamplingOptions(SKFilterMode.Nearest));
                canvas.DrawImage(mapImage, new SKRect(0, height * zoom + 40, width * zoom, height * zoom + 40 + height * zoom),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }

            return composed;
        }

        /// <summary>A managed glyph run for direct pipeline draws (no shaping — cmap and
        /// advances only, which is exactly what the rasterization tools compare).</summary>
        internal static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface,
            string text, double emSize, Point origin)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                if (!typeface.CharacterToGlyphMap.ContainsGlyph(c))
                {
                    continue;
                }

                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, origin);
        }

        /// <summary>Writes the deterministic Inter figures the docs embed.</summary>
        public static void ExportAll(string directory, GlyphTypeface typeface)
        {
            Directory.CreateDirectory(directory);

            Save(HintingAnatomy(typeface, typeface.CharacterToGlyphMap['g'], "'g'", 12, TextHintingMode.Light),
                Path.Combine(directory, "hinting-anatomy.png"));
            Save(MaskAnatomy(typeface, typeface.CharacterToGlyphMap['g'], "'g'", "Hamburg", 13),
                Path.Combine(directory, "mask-anatomy.png"));
            Save(ClearTypePipeline(typeface, typeface.CharacterToGlyphMap['e'], "'e'", 13, bgr: false, gamma: true,
                TextHintingMode.Light), Path.Combine(directory, "cleartype-pipeline.png"));
            Save(SlugBands(typeface, typeface.CharacterToGlyphMap['g'], "'g'"), Path.Combine(directory, "slug-bands.png"));
        }

        private static void Save(SKBitmap bitmap, string path)
        {
            using (bitmap)
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(path))
            {
                data.SaveTo(file);
            }
        }

        private static void DrawOutline(SKCanvas canvas, GlyphPathBuilder contours,
            Func<float, float> mapX, Func<float, float> mapY, SKColor color)
        {
            using var paint = new SKPaint { Color = color, IsStroke = true, StrokeWidth = 2, IsAntialias = true };
            using var path = new SKPath();

            var verbs = contours.Verbs;
            var points = contours.Points;
            var pointIndex = 0;

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        path.MoveTo(mapX(points[pointIndex]), mapY(points[pointIndex + 1]));
                        pointIndex += 2;
                        break;
                    case GlyphPathVerb.LineTo:
                        path.LineTo(mapX(points[pointIndex]), mapY(points[pointIndex + 1]));
                        pointIndex += 2;
                        break;
                    case GlyphPathVerb.QuadTo:
                        path.QuadTo(mapX(points[pointIndex]), mapY(points[pointIndex + 1]),
                            mapX(points[pointIndex + 2]), mapY(points[pointIndex + 3]));
                        pointIndex += 4;
                        break;
                    case GlyphPathVerb.CubicTo:
                        path.CubicTo(mapX(points[pointIndex]), mapY(points[pointIndex + 1]),
                            mapX(points[pointIndex + 2]), mapY(points[pointIndex + 3]),
                            mapX(points[pointIndex + 4]), mapY(points[pointIndex + 5]));
                        pointIndex += 6;
                        break;
                    case GlyphPathVerb.Close:
                        path.Close();
                        break;
                }
            }

            canvas.DrawPath(path, paint);
        }
    }
}
