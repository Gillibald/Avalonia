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

        /// <summary>Ambient theme override; the doc export pins Light for determinism.</summary>
        internal static FigureTheme? ThemeOverride;

        private static FigureTheme Theme => ThemeOverride ?? FigureTheme.Current;

        /// <summary>
        /// One glyph on the pixel grid: the rasterized mask, the outline before (red) and
        /// after (blue) the hinting warps, the zone rows (green, with their pre-snap source
        /// positions dashed) and the per-glyph stroke pairs (orange).
        /// </summary>
        public static SKBitmap HintingAnatomy(GlyphTypeface typeface, ushort glyph, string label, float size,
            TextHintingMode hinting, out string legend, bool embedCaption = true)
        {
            legend = "red unhinted, blue grid-fit, green zones (dashed = source), orange stroke pairs";
            var t = Theme;

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
            var height = marginTop + mask.Height * zoom + (embedCaption ? 76 : 20);

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(t.Background);

            // Mask cells and the pixel grid.
            using (var cell = new SKPaint())
            using (var grid = new SKPaint { Color = t.Grid, IsStroke = true })
            {
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var coverage = mask.IsEmpty ? (byte)0 : mask.Alpha[y * mask.Width + x];

                        if (coverage > 0)
                        {
                            cell.Color = t.Ink.WithAlpha(coverage);
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
                using var zonePaint = new SKPaint { Color = t.Zone, IsStroke = true, StrokeWidth = 2 };
                using var sourcePaint = new SKPaint
                {
                    Color = t.Zone.WithAlpha(0x90), IsStroke = true,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0),
                };
                using var font = new SKFont(SKTypeface.Default, 12);
                using var text = new SKPaint { Color = t.Zone };

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
                using var strokePaint = new SKPaint { Color = t.Stroke, IsStroke = true, StrokeWidth = 2 };
                using var font = new SKFont(SKTypeface.Default, 12);
                using var text = new SKPaint { Color = t.Stroke };
                var right = marginLeft + mask.Width * zoom;

                for (var i = 0; i < strokeKnots; i++)
                {
                    canvas.DrawLine(right, DeviceY(strokeTo[i]), right + 26, DeviceY(strokeTo[i]), strokePaint);
                    canvas.DrawText(Inv($"{strokeFrom[i]:0.##}→{strokeTo[i]:0}"), right + 30,
                        DeviceY(strokeTo[i]) + 4, SKTextAlign.Left, font, text);
                }
            }

            DrawOutline(canvas, unhinted, DeviceX, DeviceY, t.Unhinted);
            DrawOutline(canvas, hinted, DeviceX, DeviceY, t.Hinted);

            if (embedCaption)
            {
                using var font = new SKFont(SKTypeface.Default, 13);
                using var text = new SKPaint { Color = t.Label };

                canvas.DrawText(Inv($"{label} {size:0.#}px  hinting {hinting}  |  {legend}"),
                    6, height - 10, SKTextAlign.Left, font, text);
            }

            return bitmap;
        }

        /// <summary>
        /// The bytecode engine's version of the hinting figure: the mask on the grid, the
        /// unhinted outline (red), the outline the font's own programs produced (blue) at a
        /// chosen instruction step, per-point displacement indicators colored by touch axis,
        /// and the phantom points. The auto-hinter's zones and warps do not apply here.
        /// </summary>
        public static SKBitmap BytecodeHintingAnatomy(GlyphTypeface typeface, ushort glyph, string label,
            float size, TextHintingMode hinting, TrueTypeHintingProbe probe, int step,
            out string legend, bool embedCaption = true)
        {
            legend = "red unhinted, blue fitted by the font's program  |  connectors original→current" +
                Environment.NewLine +
                "points: green y-touched, orange x-touched, purple both, gray untouched, crosses phantom";
            var t = Theme;

            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var scale = scaleQ / (GlyphMaskKey.ScaleQuantum * typeface.Metrics.DesignEmHeight);
            var stemSnap = hinting == TextHintingMode.Strong;

            var mask = GlyphMasks.Build(typeface, new GlyphPathBuilder(),
                new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, GridFit: true, stemSnap));

            var unhinted = new GlyphPathBuilder();

            typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), unhinted);

            var fitted = new GlyphPathBuilder();

            probe.EmitAt(step, new Matrix(1, 0, 0, -1, 0, 0), fitted);

            var zoom = Math.Clamp(400 / Math.Max(mask.Width, Math.Max(mask.Height, 1)), 6, 30);
            const int marginLeft = 40;
            const int marginTop = 28;
            var width = Math.Max(marginLeft + mask.Width * zoom + 40, 560);
            var height = marginTop + mask.Height * zoom + (embedCaption ? 96 : 16);
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(t.Background);

            using (var cell = new SKPaint())
            using (var grid = new SKPaint { Color = t.Grid, IsStroke = true })
            {
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        var coverage = mask.IsEmpty ? (byte)0 : mask.Alpha[y * mask.Width + x];

                        if (coverage > 0)
                        {
                            cell.Color = t.Ink.WithAlpha(coverage);
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

            DrawOutline(canvas, unhinted, DeviceX, DeviceY, t.Unhinted);
            DrawOutline(canvas, fitted, DeviceX, DeviceY, t.Hinted);

            // Per-point displacement: a connector from the scaled original to the current
            // position, colored by which axes the program touched. Phantoms draw as crosses.
            var zone = probe.Zone;
            var (curX, curY, tags) = probe.StateAt(step);
            var outlinePoints = Math.Min(zone.PointCount - 4, curX.Length);
            var bothTouched = t.Both;
            var untouched = new SKColor(0x90, 0x90, 0x90);

            using (var connector = new SKPaint { IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true })
            using (var dot = new SKPaint { IsAntialias = true })
            {
                for (var i = 0; i < outlinePoints; i++)
                {
                    var fromX = DeviceX(zone.OrgX[i] / 64f);
                    var fromY = DeviceY(-zone.OrgY[i] / 64f);
                    var toX = DeviceX(curX[i] / 64f);
                    var toY = DeviceY(-curY[i] / 64f);

                    var touchX = (tags[i] & Avalonia.Media.Fonts.Rasterization.TrueType.TrueTypeZone.TouchX) != 0;
                    var touchY = (tags[i] & Avalonia.Media.Fonts.Rasterization.TrueType.TrueTypeZone.TouchY) != 0;
                    var color = touchX && touchY ? bothTouched :
                        touchY ? t.Zone :
                        touchX ? t.Stroke : untouched;

                    if (Math.Abs(toX - fromX) + Math.Abs(toY - fromY) > 1.5f)
                    {
                        connector.Color = color.WithAlpha(0xA0);
                        canvas.DrawLine(fromX, fromY, toX, toY, connector);
                        dot.Color = color.WithAlpha(0x70);
                        canvas.DrawCircle(fromX, fromY, 2f, dot);
                    }

                    dot.Color = color;
                    canvas.DrawCircle(toX, toY, 2.6f, dot);
                }

                // Points the currently shown instruction moved get a highlight ring.
                if (probe.CanScrub && step > 0 && step <= probe.StepCount)
                {
                    var (prevX, prevY, _) = probe.StateAt(step - 1);

                    using var ring = new SKPaint
                    {
                        Color = t.Ring, IsStroke = true, StrokeWidth = 2, IsAntialias = true,
                    };

                    for (var i = 0; i < outlinePoints && i < prevX.Length; i++)
                    {
                        if (curX[i] != prevX[i] || curY[i] != prevY[i])
                        {
                            canvas.DrawCircle(DeviceX(curX[i] / 64f), DeviceY(-curY[i] / 64f), 6, ring);
                        }
                    }
                }

                // Phantom points: origin, advance, top, bottom.
                using var cross = new SKPaint
                {
                    Color = t.Ink, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true,
                };

                for (var i = Math.Max(outlinePoints, 0); i < zone.PointCount && i < curX.Length; i++)
                {
                    var x = DeviceX(curX[i] / 64f);
                    var y = DeviceY(-curY[i] / 64f);

                    canvas.DrawLine(x - 4, y, x + 4, y, cross);
                    canvas.DrawLine(x, y - 4, x, y + 4, cross);
                }
            }

            if (embedCaption)
            {
                using var font = new SKFont(SKTypeface.Default, 13);
                using var text = new SKPaint { Color = t.Label };
                var engine = probe.FullInterpretation ? "full interpretation" : "v40 class (y only)";

                canvas.DrawText(
                    Inv($"{label} {size:0.#}px  hinting {hinting}  |  engine: TrueType bytecode, {engine}, {probe.InstructionsExecuted} ops"),
                    6, height - 46, SKTextAlign.Left, font, text);
                canvas.DrawText(
                    "red unhinted, blue fitted by the font's program  |  connectors original→current",
                    6, height - 28, SKTextAlign.Left, font, text);
                canvas.DrawText(
                    "points: green y-touched, orange x-touched, purple both, gray untouched, crosses phantom",
                    6, height - 10, SKTextAlign.Left, font, text);
            }

            return bitmap;
        }

        /// <summary>
        /// Mask anatomy: the focus glyph's mask magnified with its apron marked, the cache key
        /// fields, and a run composed from per-glyph masks shown at 1x and 4x.
        /// </summary>
        public static SKBitmap MaskAnatomy(GlyphTypeface typeface, ushort glyph, string label, string runText, float size,
            out string keyInfo, bool embedInfo = true)
        {
            // Compact enough for a quadrant; the run redraws at 3x instead of 4x.
            var t = Theme;
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
                    RunMaskComposer.MakeTint(255, t.Ink.Red, t.Ink.Green, t.Ink.Blue), run, runWidth, runHeight);

                typeface.TryGetGlyphMetrics(g, out var metrics);
                penX += metrics.AdvanceWidth * scale;
            }

            // Every mask variant this glyph builds at this size: the hinting ladder across
            // the three mask modes, each through the real Build with its real key.
            var variantModes = new[]
            {
                ("antialiased", GlyphMaskMode.Antialiased),
                ("subpixel", GlyphMaskMode.Subpixel),
                ("aliased", GlyphMaskMode.Aliased),
            };
            var variantHintings = new[]
            {
                ("None", false, false), ("Light", true, false), ("Strong", true, true),
            };
            var variants = new GlyphMask[variantModes.Length, variantHintings.Length];
            var variantZoom = 3;
            var variantCellWidth = 0;
            var variantCellHeight = 0;

            for (var m = 0; m < variantModes.Length; m++)
            {
                for (var h = 0; h < variantHintings.Length; h++)
                {
                    var (_, gridFit, stemSnap) = variantHintings[h];

                    variants[m, h] = GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(
                        glyph, scaleQ, 0, variantModes[m].Item2, gridFit, stemSnap));
                    variantCellWidth = Math.Max(variantCellWidth, variants[m, h].Width);
                    variantCellHeight = Math.Max(variantCellHeight, variants[m, h].Height);
                }
            }

            variantZoom = Math.Clamp(96 / Math.Max(Math.Max(variantCellWidth, variantCellHeight), 1), 1, 8);

            var variantColumnWidth = Math.Max(variantCellWidth * variantZoom + 16, 120);
            var variantRowHeight = variantCellHeight * variantZoom + 34;
            var variantTop = Math.Max(mask.Height * zoom, 126) + runHeight * 4 + 96;

            keyInfo = string.Join(Environment.NewLine,
                Inv($"GlyphMaskKey: glyph {glyph}, scaleQ {scaleQ} ({scaleQ / GlyphMaskKey.ScaleQuantum:0.###} px/em)"),
                $"phase 0 of {GlyphMaskKey.PhaseCount}, mode Antialiased, GridFit, no StemSnap",
                $"mask {mask.Width}x{mask.Height} at ({mask.Left},{mask.Top}), pen-relative",
                $"cache: {typeface.MaskCache.Count} masks resident, budget {GlyphMaskCache.DefaultBudgetBytes / (1024 * 1024)} MB");

            var width = Math.Max(Math.Max(Math.Max(maskPanelWidth + (embedInfo ? 480 : 0), runWidth * 3 + 40), 600),
                70 + variantColumnWidth * variantHintings.Length);
            var height = variantTop + 30 + variantRowHeight * variantModes.Length + 16;
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(t.Background);

            // Focus mask with apron.
            using (var cell = new SKPaint())
            using (var grid = new SKPaint { Color = t.Grid, IsStroke = true })
            using (var apron = new SKPaint
                   {
                       Color = t.Stroke, IsStroke = true, StrokeWidth = 2,
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
                            cell.Color = t.Ink.WithAlpha(coverage);
                            canvas.DrawRect(10 + x * zoom, 28 + y * zoom, zoom, zoom, cell);
                        }
                    }
                }

                canvas.DrawRect(10, 28, mask.Width * zoom, mask.Height * zoom, grid);
                canvas.DrawRect(10 + GlyphMasks.Apron * zoom, 28 + GlyphMasks.Apron * zoom,
                    (mask.Width - 2 * GlyphMasks.Apron) * zoom, (mask.Height - 2 * GlyphMasks.Apron) * zoom, apron);
            }

            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var text = new SKPaint { Color = t.Label })
            {
                canvas.DrawText($"{label} mask, dashed = {GlyphMasks.Apron}px apron", 10, 18, SKTextAlign.Left, font, text);

                if (embedInfo)
                {
                    var infoX = Math.Max(10 + mask.Width * zoom + 24, 180);
                    var infoY = 46;

                    foreach (var line in keyInfo.Split(Environment.NewLine))
                    {
                        canvas.DrawText(line, infoX, infoY, SKTextAlign.Left, font, text);
                        infoY += 20;
                    }
                }
            }

            // The composed run at 1x and 4x.
            var runTop = Math.Max(mask.Height * zoom, 126) + 52;

            using (var image = SKImage.FromPixelCopy(
                       new SKImageInfo(runWidth, runHeight, SKColorType.Bgra8888, SKAlphaType.Premul), run))
            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var text = new SKPaint { Color = t.Label })
            {
                canvas.DrawText(Inv($"run mask composed from per-glyph masks at integer pens (\"{runText}\" {size:0.#}px), 1x and 4x:"),
                    10, runTop - 8, SKTextAlign.Left, font, text);
                canvas.DrawImage(image, new SKRect(10, runTop, 10 + runWidth, runTop + runHeight));
                canvas.DrawImage(image,
                    new SKRect(10, runTop + runHeight + 8, 10 + runWidth * 3, runTop + runHeight + 8 + runHeight * 3),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }

            // The variant matrix: every (mode, hinting) mask the pipeline can build for this
            // glyph at this size, straight from Build, with its cached bounds.
            using (var font = new SKFont(SKTypeface.Default, 13))
            using (var small = new SKFont(SKTypeface.Default, 11))
            using (var text = new SKPaint { Color = t.Label })
            using (var faint = new SKPaint { Color = t.Faint })
            using (var cell = new SKPaint())
            {
                canvas.DrawText($"every mask variant at this size ({variantZoom}x):", 10, variantTop - 6,
                    SKTextAlign.Left, font, text);

                for (var h = 0; h < variantHintings.Length; h++)
                {
                    canvas.DrawText(variantHintings[h].Item1, 70 + h * variantColumnWidth, variantTop + 12,
                        SKTextAlign.Left, small, faint);
                }

                for (var m = 0; m < variantModes.Length; m++)
                {
                    var rowTop = variantTop + 22 + m * variantRowHeight;

                    canvas.DrawText(variantModes[m].Item1, 10, rowTop + 30, SKTextAlign.Left, small, faint);

                    for (var h = 0; h < variantHintings.Length; h++)
                    {
                        var variant = variants[m, h];
                        var cellX = 70 + h * variantColumnWidth;

                        if (variant.IsEmpty)
                        {
                            canvas.DrawText("empty", cellX, rowTop + 30, SKTextAlign.Left, small, faint);
                            continue;
                        }

                        for (var y = 0; y < variant.Height; y++)
                        {
                            for (var x = 0; x < variant.Width; x++)
                            {
                                if (variant.Channels == 3)
                                {
                                    var r = variant.Alpha[(y * variant.Width + x) * 3];
                                    var g = variant.Alpha[(y * variant.Width + x) * 3 + 1];
                                    var b = variant.Alpha[(y * variant.Width + x) * 3 + 2];

                                    if (r == 0 && g == 0 && b == 0)
                                    {
                                        continue;
                                    }

                                    cell.Color = t.Blend(r, g, b);
                                }
                                else
                                {
                                    var coverage = variant.Alpha[y * variant.Width + x];

                                    if (coverage == 0)
                                    {
                                        continue;
                                    }

                                    cell.Color = t.Ink.WithAlpha(coverage);
                                }

                                canvas.DrawRect(cellX + x * variantZoom, rowTop + y * variantZoom,
                                    variantZoom, variantZoom, cell);
                            }
                        }

                        canvas.DrawText(
                            Inv($"{variant.Width}x{variant.Height} @ ({variant.Left},{variant.Top})"),
                            cellX, rowTop + variantCellHeight * variantZoom + 12, SKTextAlign.Left, small, faint);
                    }
                }
            }

            return bitmap;
        }

        /// <summary>
        /// The ClearType stages side by side: the 3x horizontal analytic raster, the three
        /// FIR-filtered stripe channels, and the final composite on white (optionally without
        /// gamma correction, optionally BGR stripe order).
        /// </summary>
        public static SKBitmap ClearTypePipeline(GlyphTypeface typeface, ushort glyph, string label, float size,
            bool bgr, bool gamma, TextHintingMode hinting, out string legend, bool embedCaption = true)
        {
            legend = "3x raster → (1,2,3,2,1)/9 FIR per stripe → interleaved RGB mask → per-channel blend";
            var t = Theme;

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

            // Reproduce the 3x raster the way GlyphMasks does: through the font's own
            // programs when the bytecode engine fit this mask, the auto-warps otherwise.
            var contours = new GlyphPathBuilder();
            var lcdProbe = gridFit
                ? TrueTypeHintingProbe.TryCreate(typeface, glyph, size, GlyphMaskMode.Subpixel, stemSnap, out _)
                : null;

            if (lcdProbe is not null)
            {
                lcdProbe.EmitAt(lcdProbe.StepCount, new Matrix(3, 0, 0, -1, 0, 0), contours);
            }
            else
            {
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
            }

            var raw = new byte[mask.Width * 3 * mask.Height];

            GlyphRasterizer.Rasterize(contours, mask.Width * 3, mask.Height, -mask.Left * 3, -mask.Top, false, raw);

            const int zoom = 9;
            const int gap = 20;
            var panelWidth = mask.Width * zoom;
            var raw3Width = mask.Width * 3 * (zoom / 3);
            var width = raw3Width + (panelWidth + gap) * 4 + gap + 20;
            var height = mask.Height * zoom + (embedCaption ? 74 : 40);
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(t.Background);

            using var cell = new SKPaint();
            using var font = new SKFont(SKTypeface.Default, 13);
            using var text = new SKPaint { Color = t.Label };

            // Panel 1: raw 3x coverage.
            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width * 3; x++)
                {
                    var coverage = raw[y * mask.Width * 3 + x];

                    if (coverage > 0)
                    {
                        cell.Color = t.Ink.WithAlpha(coverage);
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

            // Panel 5: composite ink-on-background per channel; the gamma table is picked
            // for the ink color, exactly as the blender buckets by text color.
            var table = gamma ? MaskGamma.GetLcdTable(t.Ink.Red, t.Ink.Green, t.Ink.Blue) : null;
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
                        cell.Color = t.Blend(r, g, b);
                        canvas.DrawRect(compositeX + x * zoom, 28 + y * zoom, zoom, zoom, cell);
                    }
                }
            }

            canvas.DrawText($"composite ({(bgr ? "BGR" : "RGB")}, gamma {(gamma ? "on" : "off")})",
                compositeX, 18, SKTextAlign.Left, font, text);

            if (embedCaption)
            {
                canvas.DrawText(Inv($"{label} {size:0.#}px  |  {legend}"),
                    10, height - 10, SKTextAlign.Left, font, text);
            }

            return bitmap;
        }

        /// <summary>
        /// The Slug payload for one glyph: em-space quadratic chains with the horizontal and
        /// vertical band partition the shader walks, plus the payload statistics.
        /// </summary>
        public static SKBitmap SlugBands(GlyphTypeface typeface, ushort glyph, string label,
            out string info, bool embedCaption = true)
        {
            var t = Theme;
            var sink = new SlugContourSink();

            sink.Reset();

            var scale = 1.0 / typeface.Metrics.DesignEmHeight;
            var data = typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, scale, 0, 0), sink)
                ? SlugBandEncoder.Encode(sink)
                : null;

            var figureHeight = embedCaption ? 520 : 452;
            var bitmap = new SKBitmap(new SKImageInfo(560, figureHeight, SKColorType.Bgra8888, SKAlphaType.Premul));

            using var canvas = new SKCanvas(bitmap);
            using var font = new SKFont(SKTypeface.Default, 13);
            using var text = new SKPaint { Color = t.Label };

            canvas.Clear(t.Background);

            if (data is null)
            {
                info = $"{label}: no Slug payload (no contours or caps exceeded — the tier declines)";
                canvas.DrawText(info, 10, 30, SKTextAlign.Left, font, text);
                return bitmap;
            }

            const float left = 46;
            const float top = 32;
            var extent = Math.Max(data.MaxX - data.MinX, data.MaxY - data.MinY);
            var s = 370f / extent;

            float MapX(float x) => left + (x - data.MinX) * s;
            float MapY(float y) => top + (data.MaxY - y) * s;   // em space is y-up

            using (var bandPaint = new SKPaint { Color = t.Zone.WithAlpha(0x60), IsStroke = true })
            using (var bandText = new SKPaint { Color = t.Zone })
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
                       Color = t.Ink, IsStroke = true, StrokeWidth = 2, IsAntialias = true,
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

            info = $"{label}: {data.ContourCount} contours, {data.TotalCurveCount} quadratic curves, " +
                $"{data.HorizontalBandCount}x{data.VerticalBandCount} bands (worst list {worstBand} of {SlugTexelSerializer.MaxBandListLength}), " +
                $"payload {data.RetainedBytes} B, {data.FillRule}" + Environment.NewLine +
                "green counts = curves per band list; encoded once per glyph ever, in em space";

            if (embedCaption)
            {
                canvas.DrawText(
                    $"{label}: {data.ContourCount} contours, {data.TotalCurveCount} quadratic curves, " +
                    $"{data.HorizontalBandCount}x{data.VerticalBandCount} bands (worst list {worstBand} of {SlugTexelSerializer.MaxBandListLength}), " +
                    $"payload {data.RetainedBytes} B, {data.FillRule}",
                    10, figureHeight - 40, SKTextAlign.Left, font, text);
                canvas.DrawText(
                    "green counts = curves per band list; encoded once per glyph ever, in em space",
                    10, figureHeight - 20, SKTextAlign.Left, font, text);
            }

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
            var t = Theme;
            var inkBrush = new SolidColorBrush(Color.FromRgb(t.Ink.Red, t.Ink.Green, t.Ink.Blue));
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

            surface.Canvas.Clear(t.Background);

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

                        context.DrawGlyphRun(inkBrush, run);
                        y += rowHeights[row];
                    }

                    context.PopTextOptions();
                }
            }

            using (var font = new SKFont(SKTypeface.Default, 12))
            using (var label = new SKPaint { Color = t.Faint })
            {
                for (var column = 0; column < hintings.Length; column++)
                {
                    // "Unspecified" alone reads as a bug; name the policy that resolves it.
                    var header = hintings[column] == TextHintingMode.Unspecified
                        ? "Unspecified (font gasp policy)"
                        : $"{hintings[column]}";

                    surface.Canvas.DrawText(header, labelWidth + columnWidth * column + 8, 16,
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
        /// pixel (|R-B| beyond a threshold) by polarity: for dark ink on a light ground, warm
        /// on a left edge and cool on a right edge is physically correct for RGB stripes;
        /// light ink on a dark ground mirrors the sides (the first lit subpixels at a stem's
        /// left edge are the pixel's blue end). Anything else is flagged. The returned strip
        /// stacks the rendering over the classification map, and the counters come back for
        /// the caller's stats line.
        /// </summary>
        public static SKBitmap FringeAnalysis(GlyphTypeface typeface, string text, float size,
            TextHintingMode hinting, int zoom,
            out int fringed, out int warmEdges, out int coolEdges, out int wrongPolarity, out int inkPixels)
        {
            var t = Theme;
            var inkBrush = new SolidColorBrush(Color.FromRgb(t.Ink.Red, t.Ink.Green, t.Ink.Blue));
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

            surface.Canvas.Clear(t.Background);

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

                context.DrawGlyphRun(inkBrush, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            using var render = new SKBitmap(info);

            snapshot.ReadPixels(info, render.GetPixels(), render.RowBytes, 0, 0);

            fringed = 0;
            warmEdges = 0;
            coolEdges = 0;
            wrongPolarity = 0;
            inkPixels = 0;

            using var map = new SKBitmap(info);

            static int Luma(SKColor c) => 54 * c.Red + 183 * c.Green + 19 * c.Blue;

            var bgLuma = Luma(t.Background);
            var darkInk = !t.IsDark;

            bool IsInk(SKColor c) => darkInk ? Luma(c) < 240 * 256 : Luma(c) > bgLuma + 15 * 256;
            bool Inkier(SKColor a, SKColor b) => darkInk ? Luma(a) < Luma(b) : Luma(a) > Luma(b);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = render.GetPixel(x, y);

                    if (IsInk(pixel))
                    {
                        inkPixels++;
                    }

                    if (x == 0 || x == width - 1 || Math.Abs(pixel.Red - pixel.Blue) <= 8)
                    {
                        map.SetPixel(x, y, t.Background);
                        continue;
                    }

                    fringed++;

                    var leftInk = Inkier(render.GetPixel(x - 1, y), pixel);
                    var rightInk = Inkier(render.GetPixel(x + 1, y), pixel);
                    var onLeftEdge = rightInk && !leftInk;    // the stem continues rightward
                    var onRightEdge = leftInk && !rightInk;
                    var warmHealthy = darkInk ? onLeftEdge : onRightEdge;
                    var coolHealthy = darkInk ? onRightEdge : onLeftEdge;

                    if (pixel.Red > pixel.Blue && warmHealthy)
                    {
                        warmEdges++;
                        map.SetPixel(x, y, new SKColor(0xE0, 0x50, 0x30));
                    }
                    else if (pixel.Blue > pixel.Red && coolHealthy)
                    {
                        coolEdges++;
                        map.SetPixel(x, y, new SKColor(0x30, 0x60, 0xE0));
                    }
                    else if ((pixel.Red > pixel.Blue && coolHealthy) ||
                             (pixel.Blue > pixel.Red && warmHealthy))
                    {
                        // Warm where cool belongs or the reverse — swapped stripe order
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
            using (var label = new SKPaint { Color = t.Label })
            {
                canvas.Clear(t.Background);
                canvas.DrawText(Inv($"rendered output ({zoom}x)"), 0, 14, SKTextAlign.Left, font, label);
                canvas.DrawText(darkInk
                        ? "fringe map: red = warm left edge, blue = cool right edge, magenta = WRONG polarity, amber = interior"
                        : "fringe map: red = warm right edge, blue = cool left edge, magenta = WRONG polarity, amber = interior",
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

            // Doc figures embed into fixed documentation: pin the light palette so exports
            // stay deterministic whatever theme the app happens to run in.
            ThemeOverride = FigureTheme.Light;

            try
            {
                Save(HintingAnatomy(typeface, typeface.CharacterToGlyphMap['g'], "'g'", 12, TextHintingMode.Light, out _),
                    Path.Combine(directory, "hinting-anatomy.png"));
                Save(MaskAnatomy(typeface, typeface.CharacterToGlyphMap['g'], "'g'", "Hamburg", 13, out _),
                    Path.Combine(directory, "mask-anatomy.png"));
                Save(ClearTypePipeline(typeface, typeface.CharacterToGlyphMap['e'], "'e'", 13, bgr: false, gamma: true,
                    TextHintingMode.Light, out _), Path.Combine(directory, "cleartype-pipeline.png"));
                Save(SlugBands(typeface, typeface.CharacterToGlyphMap['g'], "'g'", out _), Path.Combine(directory, "slug-bands.png"));
            }
            finally
            {
                ThemeOverride = null;
            }
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
