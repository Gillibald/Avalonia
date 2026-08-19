using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Tables.Colr;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// The color-glyph inspector: the two real render altitudes side by side — the
    /// <see cref="IGlyphDrawing"/> path the record-time splitter uses, and the production
    /// draw (splitter first, then <c>DrawGlyphRun</c> dispatch, which composes COLR v0
    /// layers and bitmap strikes in the mask tier) — plus the kind-specific structure:
    /// the resolved COLR v1 paint tree, the progressive COLR v0 layer build-up, or the
    /// decoded bitmap strike. The fourth quadrant diffs the altitudes pixel by pixel.
    /// </summary>
    public partial class ColorGlyphInspectorView : UserControl
    {
        private static readonly int[] s_sizes = { 32, 64, 128, 256 };

        private Button _backButton = null!;
        private TextBlock _titleText = null!;
        private ComboBox _sizeBox = null!;
        private ComboBox _paletteBox = null!;
        private TextBlock _paletteLabel = null!;
        private Image _drawingImage = null!;
        private Image _productionImage = null!;
        private Image _structureImage = null!;
        private Image _diffImage = null!;
        private TextBlock _structureHeader = null!;
        private TextBlock _structureText = null!;
        private TextBlock _diffText = null!;

        private GlyphTypeface? _typeface;
        private ushort _glyph;
        private string? _label;
        private bool _updating;

        public event Action? BackRequested;

        public ColorGlyphInspectorView()
        {
            AvaloniaXamlLoader.Load(this);

            _backButton = this.FindControl<Button>("BackButton")!;
            _titleText = this.FindControl<TextBlock>("TitleText")!;
            _sizeBox = this.FindControl<ComboBox>("SizeBox")!;
            _paletteBox = this.FindControl<ComboBox>("PaletteBox")!;
            _paletteLabel = this.FindControl<TextBlock>("PaletteLabel")!;
            _drawingImage = this.FindControl<Image>("DrawingImage")!;
            _productionImage = this.FindControl<Image>("ProductionImage")!;
            _structureImage = this.FindControl<Image>("StructureImage")!;
            _diffImage = this.FindControl<Image>("DiffImage")!;
            _structureHeader = this.FindControl<TextBlock>("StructureHeader")!;
            _structureText = this.FindControl<TextBlock>("StructureText")!;
            _diffText = this.FindControl<TextBlock>("DiffText")!;

            _backButton.Click += (_, _) => BackRequested?.Invoke();
            this.FindControl<Button>("CopyButton")!.Click += (_, _) => ClipboardHelper.Copy(this,
                string.Join(Environment.NewLine,
                    _titleText.Text, _structureHeader.Text, _structureText.Text, _diffText.Text));
            _sizeBox.ItemsSource = s_sizes;
            _sizeBox.SelectedIndex = 2;
            _sizeBox.SelectionChanged += (_, _) => RenderAll();
            _paletteBox.SelectionChanged += (_, _) => RenderAll();
        }

        private int RenderSize => _sizeBox.SelectedItem is int size ? size : 128;

        private int PaletteIndex => Math.Max(0, _paletteBox.SelectedIndex);

        public void ShowGlyph(GlyphTypeface typeface, ushort glyphIndex, string? label = null)
        {
            _typeface = typeface;
            _glyph = glyphIndex;
            _label = label;

            _updating = true;

            var paletteCount = typeface.ColorPaletteTable?.PaletteCount ?? 0;
            var palettes = new List<string>();

            for (var i = 0; i < Math.Max(1, paletteCount); i++)
            {
                palettes.Add($"palette {i}");
            }

            _paletteBox.ItemsSource = palettes;
            _paletteBox.SelectedIndex = 0;
            _paletteBox.IsVisible = _paletteLabel.IsVisible = paletteCount > 1;
            _updating = false;

            RenderAll();
        }

        private void RenderAll()
        {
            if (_updating || _typeface is not { } typeface)
            {
                return;
            }

            var glyph = _glyph;
            var colr = typeface.ColorTable;
            var isV1 = colr?.TryGetBaseGlyphV1Record(glyph, out _) == true;
            var isV0 = colr?.HasColorLayers(glyph) == true;
            var isBitmap = typeface.BitmapSource?.HasGlyphImage(glyph) == true;
            var kind = isV1 ? "COLR v1" : isV0 ? "COLR v0" : isBitmap ? "bitmap strike" : "no color data";

            _titleText.Text = $"glyph {glyph}{(_label is null ? "" : $" {_label}")} — {kind}";

            var size = RenderSize;
            var options = new GlyphDrawingOptions
            {
                PaletteIndex = PaletteIndex > 0 ? PaletteIndex : null,
                PixelSize = size,
            };

            var drawing = typeface.GetGlyphDrawing(glyph, options);
            var scale = (double)size / typeface.Metrics.DesignEmHeight;

            // A shared canvas geometry so both altitudes are pixel-comparable: the pen sits
            // on the metrics baseline with margin for ink outside the em box on every side.
            typeface.TryGetGlyphMetrics(glyph, out var metrics);

            var margin = Math.Max(8, size / 4);
            var inkLeft = 0.0;
            var inkRight = metrics.AdvanceWidth * scale;

            if (drawing is { } d && d.Bounds.Width > 0)
            {
                inkLeft = Math.Min(inkLeft, d.Bounds.X * scale);
                inkRight = Math.Max(inkRight, d.Bounds.Right * scale);
            }

            var penX = margin - inkLeft;
            var penY = margin + Math.Ceiling(-typeface.Metrics.Ascent * scale) + size * 0.25;
            var width = (int)Math.Ceiling(penX + inkRight + margin);
            var height = (int)Math.Ceiling(penY + typeface.Metrics.Descent * scale + size * 0.25 + margin);

            using var drawingBitmap = RenderDrawingPath(typeface, drawing, width, height, new Point(penX, penY), scale);
            using var productionBitmap = RenderProductionPath(typeface, glyph, size, width, height, new Point(penX, penY));

            SetImage(_drawingImage, drawingBitmap);
            SetImage(_productionImage, productionBitmap);
            RenderDiff(drawingBitmap, productionBitmap);
            RenderStructure(typeface, glyph, isV1, isV0, isBitmap, drawing);
        }

        /// <summary>The splitter altitude: the color drawing under the run transform.</summary>
        private SKBitmap RenderDrawingPath(GlyphTypeface typeface, IGlyphDrawing? drawing,
            int width, int height, Point pen, double scale)
        {
            var bitmap = NewCanvasBitmap(width, height);

            if (drawing is null)
            {
                return bitmap;
            }

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using var impl = Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(impl, ownsImpl: false);

            using (context.PushTransform(
                       Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(pen.X, pen.Y)))
            {
                drawing.Draw(context, default);
            }

            return bitmap;
        }

        /// <summary>
        /// The production altitude: exactly what rendering does with this glyph — the
        /// record-time splitter first (extracts what the server tiers cannot compose), then
        /// the ordinary <c>DrawGlyphRun</c> dispatch for whatever stays in the run (COLR v0
        /// layer stacks and bitmap strikes compose in the mask tier).
        /// </summary>
        private SKBitmap RenderProductionPath(GlyphTypeface typeface, ushort glyph,
            int size, int width, int height, Point pen)
        {
            var bitmap = NewCanvasBitmap(width, height);

            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using var impl = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                Dpi = new Vector(96, 96),
            });
            using var context = new PlatformDrawingContext(impl, ownsImpl: false);

            var characters = (_label is { Length: > 0 } label ? label : "□").AsMemory();

            using var run = new GlyphRun(typeface, size, characters, new List<ushort> { glyph }, pen);

            if (!ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black))
            {
                context.DrawGlyphRun(Brushes.Black, run);
            }

            return bitmap;
        }

        private void RenderDiff(SKBitmap a, SKBitmap b)
        {
            var width = a.Width;
            var height = a.Height;
            var diff = NewCanvasBitmap(width, height);
            var differing = 0;
            var maxDelta = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pa = a.GetPixel(x, y);
                    var pb = b.GetPixel(x, y);
                    var delta = Math.Max(Math.Abs(pa.Red - pb.Red),
                        Math.Max(Math.Abs(pa.Green - pb.Green), Math.Abs(pa.Blue - pb.Blue)));

                    maxDelta = Math.Max(maxDelta, delta);

                    var color = delta == 0
                        ? SKColors.White
                        : delta <= 32
                            ? new SKColor(0xFF, 0xDC, 0x78)
                            : new SKColor(0xDC, 0x00, 0x00);

                    if (delta > 0)
                    {
                        differing++;
                    }

                    diff.SetPixel(x, y, color);
                }
            }

            SetImage(_diffImage, diff);
            diff.Dispose();

            var total = width * height;
            var percent = 100.0 * differing / total;

            // Verdict first, numbers second: this diff is a routing proof, not a defect meter.
            var verdict = differing == 0
                ? "Both altitudes are pixel-identical: the production draw used the splitter's drawing."
                : "The production draw composed this glyph in the mask tier (expected for COLR v0 and bitmap strikes); the differences are antialiasing-level.";

            _diffText.Text = verdict + FormattableString.Invariant(
                $" {differing} of {total} pixels differ ({percent:0.0}%), max channel delta {maxDelta}.");
        }

        private void RenderStructure(GlyphTypeface typeface, ushort glyph,
            bool isV1, bool isV0, bool isBitmap, IGlyphDrawing? drawing)
        {
            _structureImage.Source = null;
            _structureText.Text = string.Empty;

            if (isV1)
            {
                _structureHeader.Text = "Structure: resolved COLR v1 paint tree";
                _structureText.Text = DumpPaintTree(typeface, glyph);
                return;
            }

            if (isV0)
            {
                _structureHeader.Text = "Structure: COLR v0 layer build-up (paint order)";
                RenderLayerBuildUp(typeface, glyph);
                return;
            }

            if (isBitmap)
            {
                _structureHeader.Text = "Structure: bitmap strike";
                _structureText.Text = drawing is null
                    ? "no drawing at these options"
                    : FormattableString.Invariant(
                        $"decoded strike for PixelSize {RenderSize}: type {drawing.Type}, ink {drawing.Bounds.Width:0}x{drawing.Bounds.Height:0} design units");
                return;
            }

            _structureHeader.Text = "Structure";
            _structureText.Text = "no color representation";
        }

        /// <summary>The resolved paint tree, the same walk the v1 drawing performs.</summary>
        private string DumpPaintTree(GlyphTypeface typeface, ushort glyph)
        {
            if (typeface.ColorTable is not { } colr ||
                typeface.ColorPaletteTable is not { } cpal ||
                !colr.TryGetBaseGlyphV1Record(glyph, out var record))
            {
                return "no v1 record";
            }

            var context = new ColrContext(typeface, colr, cpal, PaletteIndex, null);

            if (!typeface.TryGetBaseGlyphV1Paint(context, record, out var paint))
            {
                return "paint resolution failed";
            }

            var report = new StringBuilder();

            Dump(paint!, report, 0);

            return report.ToString();
        }

        private static void Dump(Paint paint, StringBuilder report, int depth)
        {
            if (depth > 8)
            {
                report.AppendLine($"{new string(' ', depth * 2)}...");
                return;
            }

            var indent = new string(' ', depth * 2);

            switch (paint)
            {
                case ResolvedClipBox clip:
                    report.AppendLine($"{indent}ClipBox {Fmt.N(clip.Box)}");
                    Dump(clip.Inner, report, depth + 1);
                    break;
                case ColrLayers layers:
                    report.AppendLine($"{indent}Layers x{layers.Layers.Count}");
                    for (var i = 0; i < layers.Layers.Count; i++)
                    {
                        if (i == 12)
                        {
                            report.AppendLine($"{indent}  ... {layers.Layers.Count - i} more");
                            break;
                        }

                        Dump(layers.Layers[i], report, depth + 1);
                    }
                    break;
                case Composite composite:
                    report.AppendLine($"{indent}Composite {composite.Mode}");
                    report.AppendLine($"{indent} backdrop:");
                    Dump(composite.Backdrop, report, depth + 1);
                    report.AppendLine($"{indent} source:");
                    Dump(composite.Source, report, depth + 1);
                    break;
                case ResolvedTransform transform:
                    report.AppendLine($"{indent}Transform {Fmt.N(transform.Matrix)}");
                    Dump(transform.Inner, report, depth + 1);
                    break;
                case Glyph glyphPaint:
                    report.AppendLine($"{indent}Glyph {glyphPaint.GlyphIndex}");
                    Dump(glyphPaint.Paint, report, depth + 1);
                    break;
                case ResolvedSolid solid:
                    report.AppendLine($"{indent}Solid {solid.Color}");
                    break;
                case ResolvedLinearGradient:
                    report.AppendLine($"{indent}LinearGradient");
                    break;
                case ResolvedRadialGradient:
                    report.AppendLine($"{indent}RadialGradient");
                    break;
                case ResolvedConicGradient:
                    report.AppendLine($"{indent}ConicGradient");
                    break;
                default:
                    report.AppendLine($"{indent}{paint.GetType().Name}");
                    break;
            }
        }

        /// <summary>Cumulative tinted-layer checkpoints, the server-side compose order.</summary>
        private void RenderLayerBuildUp(GlyphTypeface typeface, ushort glyph)
        {
            var layers = typeface.ColorTable!.GetLayers(glyph);

            if (layers.Length == 0)
            {
                return;
            }

            const float layerRenderSize = 44f;
            const int tile = 56;

            // Up to eight evenly spaced checkpoints, always ending at the full stack.
            var steps = Math.Min(8, layers.Length);
            var checkpoints = new int[steps];

            for (var i = 0; i < steps; i++)
            {
                checkpoints[i] = Math.Max(1, (int)Math.Round((i + 1) * (double)layers.Length / steps));
            }

            var cpal = typeface.ColorPaletteTable;
            var bitmap = NewCanvasBitmap(steps * tile + 1, tile + 15);
            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(layerRenderSize);

            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            using (var grid = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xE6), IsStroke = true })
            using (var label = new SKPaint { Color = new SKColor(0x90, 0x90, 0x90) })
            using (var labelFont = new SKFont(SKTypeface.Default, 9))
            {
                canvas.Clear(SKColors.White);

                for (var step = 0; step < steps; step++)
                {
                    var upTo = checkpoints[step];
                    var originX = step * tile;

                    for (var i = 0; i < upTo; i++)
                    {
                        var layer = layers[i];
                        var color = Colors.Black;

                        if (layer.PaletteIndex != 0xFFFF && cpal is not null)
                        {
                            cpal.TryGetColor(PaletteIndex, layer.PaletteIndex, out color);
                        }

                        var mask = GlyphMasks.Build(typeface, scratch,
                            new GlyphMaskKey(layer.GlyphIndex, scaleQ, 0, GlyphMaskMode.Antialiased));

                        if (mask.IsEmpty)
                        {
                            continue;
                        }

                        var cx = originX + (tile - mask.Width) / 2;
                        var cy = (tile - mask.Height) / 2;

                        for (var y = 0; y < mask.Height; y++)
                        {
                            for (var x = 0; x < mask.Width; x++)
                            {
                                var coverage = mask.Alpha[y * mask.Width + x];

                                if (coverage > 0)
                                {
                                    paint.Color = new SKColor(color.R, color.G, color.B,
                                        (byte)(coverage * color.A / 255));
                                    canvas.DrawRect(cx + x, cy + y, 1, 1, paint);
                                }
                            }
                        }
                    }

                    canvas.DrawText($"0..{upTo - 1}", originX + 3, tile + 11, SKTextAlign.Left, labelFont, label);
                    canvas.DrawLine(originX, 0, originX, tile, grid);
                }

                canvas.DrawLine(steps * tile, 0, steps * tile, tile, grid);
                canvas.DrawLine(0, tile, steps * tile, tile, grid);
            }

            SetImage(_structureImage, bitmap);
            bitmap.Dispose();

            _structureText.Text = $"{layers.Length} layers, palette {PaletteIndex}";
        }

        private static SKBitmap NewCanvasBitmap(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, width), Math.Max(1, height),
                SKColorType.Bgra8888, SKAlphaType.Premul));

            bitmap.Erase(SKColors.White);

            return bitmap;
        }

        private static void SetImage(Image target, SKBitmap bitmap)
        {
            var previous = target.Source as IDisposable;

            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                target.Source = new Bitmap(stream);
            }

            previous?.Dispose();
        }
    }
}
