using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// The dedicated color-glyph view: every glyph with a color representation (COLR v0
    /// layers, COLR v1 paint graph, or a bitmap strike) rendered in color through the same
    /// <see cref="IGlyphDrawing"/> path the renderer uses when it splits color glyphs out of
    /// a run. The palette selector exercises CPAL, the size selector picks bitmap strikes,
    /// and selecting a v0 glyph shows its tinted layer stack.
    /// </summary>
    public partial class ColorGlyphsView : UserControl
    {
        private const int Columns = 8;
        private const int Rows = 8;
        private const int PageSize = Columns * Rows;
        private const int LabelStrip = 14;

        private static readonly int[] s_sizes = { 24, 32, 48, 64, 96 };

        private ComboBox _sizeBox = null!;
        private ComboBox _paletteBox = null!;
        private TextBlock _paletteLabel = null!;
        private Button _prevButton = null!;
        private Button _nextButton = null!;
        private TextBlock _pageText = null!;
        private TextBlock _summaryText = null!;
        private TextBlock _infoText = null!;
        private TextBlock _layersHeader = null!;
        private TextBlock _layersText = null!;
        private Image _gridImage = null!;
        private Image _previewImage = null!;
        private Image _layersImage = null!;

        private GlyphTypeface? _typeface;
        private readonly List<ushort> _glyphs = new();
        private int _page;
        private int _selectedIndex = -1;

        public ColorGlyphsView()
        {
            AvaloniaXamlLoader.Load(this);

            _sizeBox = this.FindControl<ComboBox>("SizeBox")!;
            _paletteBox = this.FindControl<ComboBox>("PaletteBox")!;
            _paletteLabel = this.FindControl<TextBlock>("PaletteLabel")!;
            _prevButton = this.FindControl<Button>("PrevButton")!;
            _nextButton = this.FindControl<Button>("NextButton")!;
            _pageText = this.FindControl<TextBlock>("PageText")!;
            _summaryText = this.FindControl<TextBlock>("SummaryText")!;
            _infoText = this.FindControl<TextBlock>("InfoText")!;
            _layersHeader = this.FindControl<TextBlock>("LayersHeader")!;
            _layersText = this.FindControl<TextBlock>("LayersText")!;
            _gridImage = this.FindControl<Image>("GridImage")!;
            _previewImage = this.FindControl<Image>("PreviewImage")!;
            _layersImage = this.FindControl<Image>("LayersImage")!;

            _sizeBox.ItemsSource = s_sizes;
            _sizeBox.SelectedIndex = 2;
            _sizeBox.SelectionChanged += (_, _) => Refresh();
            _paletteBox.SelectionChanged += (_, _) => Refresh();
            _prevButton.Click += (_, _) => ShowPage(_page - 1);
            _nextButton.Click += (_, _) => ShowPage(_page + 1);
            _gridImage.PointerPressed += OnGridPressed;
        }

        private int RenderSize => _sizeBox.SelectedItem is int size ? size : 48;

        private int CellSize => RenderSize + LabelStrip + 8;

        private GlyphDrawingOptions DrawingOptions => new()
        {
            PaletteIndex = _paletteBox.SelectedIndex > 0 ? _paletteBox.SelectedIndex : null,
            PixelSize = RenderSize,
        };

        public void SetTypeface(GlyphTypeface? typeface)
        {
            if (ReferenceEquals(_typeface, typeface))
            {
                return;
            }

            _typeface = typeface;

            var paletteCount = typeface?.ColorPaletteTable?.PaletteCount ?? 0;
            var palettes = new List<string>();

            for (var i = 0; i < Math.Max(1, paletteCount); i++)
            {
                palettes.Add($"palette {i}");
            }

            _paletteBox.ItemsSource = palettes;
            _paletteBox.SelectedIndex = 0;
            _paletteBox.IsVisible = _paletteLabel.IsVisible = paletteCount > 1;

            RebuildList();
        }

        private void Refresh()
        {
            _selectedIndex = -1;
            UpdateInfo();
            RenderPage();
        }

        private void RebuildList()
        {
            _glyphs.Clear();
            _selectedIndex = -1;

            if (_typeface is { } typeface)
            {
                var colr = typeface.ColorTable;
                var bitmaps = typeface.BitmapSource;

                for (var id = 0; id < typeface.GlyphCount && id <= ushort.MaxValue; id++)
                {
                    var glyph = (ushort)id;
                    var isColor = (colr is not null &&
                                   (colr.HasColorLayers(glyph) || colr.TryGetBaseGlyphV1Record(glyph, out _))) ||
                                  bitmaps?.HasGlyphImage(glyph) == true;

                    if (isColor)
                    {
                        _glyphs.Add(glyph);
                    }
                }
            }

            _summaryText.Text = _typeface is null
                ? "no typeface"
                : _glyphs.Count == 0
                    ? $"{_typeface.FamilyName}: no color glyphs"
                    : $"{_typeface.FamilyName}: {_glyphs.Count} color glyphs";

            ShowPage(0);
        }

        private void ShowPage(int page)
        {
            var pageCount = Math.Max(1, (_glyphs.Count + PageSize - 1) / PageSize);

            _page = Math.Clamp(page, 0, pageCount - 1);
            _pageText.Text = $"page {_page + 1} / {pageCount}";
            _selectedIndex = -1;
            UpdateInfo();
            RenderPage();
        }

        private void RenderPage()
        {
            if (_typeface is not { } typeface || _glyphs.Count == 0)
            {
                _gridImage.Source = null;
                return;
            }

            var cell = CellSize;
            var options = DrawingOptions;
            var scale = (double)RenderSize / typeface.Metrics.DesignEmHeight;
            var bitmap = new SKBitmap(new SKImageInfo(Columns * cell + 1, Rows * cell + 1,
                SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                var first = _page * PageSize;

                // Color ink renders through the real drawing path: a DrawingContext over this
                // canvas, one scale-and-translate per cell, exactly like the run splitter.
                using (var contextImpl = Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(
                           canvas, new Vector(96, 96)))
                using (var context = new PlatformDrawingContext(contextImpl, ownsImpl: false))
                {
                    for (var i = 0; i < PageSize && first + i < _glyphs.Count; i++)
                    {
                        var glyph = _glyphs[first + i];

                        if (typeface.GetGlyphDrawing(glyph, options) is not { } drawing)
                        {
                            continue;
                        }

                        var cx = (i % Columns) * cell;
                        var cy = (i / Columns) * cell;
                        var bounds = drawing.Bounds;

                        if (bounds.Width <= 0 || bounds.Height <= 0)
                        {
                            continue;
                        }

                        // Fit and center the drawing's ink box in the cell's upper region:
                        // the em scale unless the ink overflows the cell, then shrink to fit.
                        var fit = scale;

                        if (bounds.Width * fit > cell - 8)
                        {
                            fit = (cell - 8) / bounds.Width;
                        }

                        if (bounds.Height * fit > cell - LabelStrip - 8)
                        {
                            fit = Math.Min(fit, (cell - LabelStrip - 8) / bounds.Height);
                        }

                        var penX = cx + (cell - bounds.Width * fit) / 2 - bounds.X * fit;
                        var penY = cy + (cell - LabelStrip - bounds.Height * fit) / 2 - bounds.Y * fit;

                        using (context.PushTransform(
                                   Matrix.CreateScale(fit, fit) * Matrix.CreateTranslation(penX, penY)))
                        {
                            drawing.Draw(context, default);
                        }
                    }
                }

                using (var grid = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xE6), IsStroke = true })
                using (var label = new SKPaint { Color = new SKColor(0x90, 0x90, 0x90) })
                using (var labelFont = new SKFont(SKTypeface.Default, 9))
                using (var selection = new SKPaint { Color = new SKColor(0x22, 0x44, 0xCC), IsStroke = true, StrokeWidth = 2 })
                {
                    for (var i = 0; i < PageSize && first + i < _glyphs.Count; i++)
                    {
                        var cx = (i % Columns) * cell;
                        var cy = (i / Columns) * cell;

                        canvas.DrawText($"{_glyphs[first + i]}", cx + 3, cy + cell - 3,
                            SKTextAlign.Left, labelFont, label);

                        if (first + i == _selectedIndex)
                        {
                            canvas.DrawRect(cx + 1, cy + 1, cell - 2, cell - 2, selection);
                        }
                    }

                    for (var x = 0; x <= Columns; x++)
                    {
                        canvas.DrawLine(x * cell, 0, x * cell, Rows * cell, grid);
                    }

                    for (var y = 0; y <= Rows; y++)
                    {
                        canvas.DrawLine(0, y * cell, Columns * cell, y * cell, grid);
                    }
                }
            }

            ReplaceImage(_gridImage, bitmap);
        }

        private void OnGridPressed(object? sender, PointerPressedEventArgs e)
        {
            var cell = CellSize;
            var position = e.GetPosition(_gridImage);
            var column = (int)(position.X / cell);
            var row = (int)(position.Y / cell);

            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            {
                return;
            }

            var index = _page * PageSize + row * Columns + column;

            if (index >= _glyphs.Count)
            {
                return;
            }

            _selectedIndex = index;
            UpdateInfo();
            RenderPage();
        }

        private void UpdateInfo()
        {
            _layersHeader.IsVisible = false;
            _layersText.Text = string.Empty;
            _layersImage.Source = null;
            _previewImage.Source = null;

            if (_typeface is not { } typeface || _selectedIndex < 0 || _selectedIndex >= _glyphs.Count)
            {
                _infoText.Text = _glyphs.Count == 0 ? "font has no color glyphs" : "click a cell";
                return;
            }

            var glyph = _glyphs[_selectedIndex];
            var lines = new List<string> { $"glyph id: {glyph}" };
            var colr = typeface.ColorTable;

            var isV1 = colr?.TryGetBaseGlyphV1Record(glyph, out _) == true;
            var isV0 = colr?.HasColorLayers(glyph) == true;
            var isBitmap = typeface.BitmapSource?.HasGlyphImage(glyph) == true;

            if (isV1)
            {
                lines.Add("kind: COLR v1 paint graph");
            }

            if (isV0)
            {
                lines.Add($"kind: COLR v0 ({colr!.GetLayers(glyph).Length} layers)");
            }

            if (isBitmap)
            {
                lines.Add("kind: bitmap strike");
            }

            if (typeface.GetGlyphDrawing(glyph, DrawingOptions) is { } drawing)
            {
                lines.Add(FormattableString.Invariant(
                    $"drawing: {drawing.Type}, ink {drawing.Bounds.Width:0}x{drawing.Bounds.Height:0} design units"));
                RenderPreview(typeface, drawing);
            }
            else
            {
                lines.Add("drawing: none at these options");
            }

            if (isV0)
            {
                RenderLayers(typeface, glyph);
            }

            _infoText.Text = string.Join(Environment.NewLine, lines);
        }

        /// <summary>The selected glyph at 128 px through the same drawing path as the grid.</summary>
        private void RenderPreview(GlyphTypeface typeface, IGlyphDrawing drawing)
        {
            const int previewSize = 128;

            var bounds = drawing.Bounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var scale = Math.Min(previewSize / bounds.Width, previewSize / bounds.Height);
            var bitmap = new SKBitmap(new SKImageInfo(previewSize + 8, previewSize + 8,
                SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                using var contextImpl = Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(
                    canvas, new Vector(96, 96));
                using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

                var penX = 4 + (previewSize - bounds.Width * scale) / 2 - bounds.X * scale;
                var penY = 4 + (previewSize - bounds.Height * scale) / 2 - bounds.Y * scale;

                using (context.PushTransform(
                           Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(penX, penY)))
                {
                    drawing.Draw(context, default);
                }
            }

            ReplaceImage(_previewImage, bitmap);
        }

        /// <summary>The COLR v0 layer stack: each layer's outline mask tinted with its
        /// resolved CPAL color, in paint order.</summary>
        private void RenderLayers(GlyphTypeface typeface, ushort glyph)
        {
            var layers = typeface.ColorTable!.GetLayers(glyph);

            if (layers.Length == 0)
            {
                return;
            }

            const int tile = 40;
            const float layerRenderSize = 32f;

            var palette = _paletteBox.SelectedIndex > 0 ? _paletteBox.SelectedIndex : 0;
            var cpal = typeface.ColorPaletteTable;
            var columns = Math.Min(layers.Length, 6);
            var rows = (layers.Length + columns - 1) / columns;
            var bitmap = new SKBitmap(new SKImageInfo(columns * tile + 1, rows * tile + 1,
                SKColorType.Bgra8888, SKAlphaType.Premul));
            var lines = new List<string>();
            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(layerRenderSize);

            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            using (var grid = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xE6), IsStroke = true })
            {
                canvas.Clear(SKColors.White);

                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    var isForeground = layer.PaletteIndex == 0xFFFF;
                    var color = Colors.Black;

                    if (!isForeground && cpal is not null)
                    {
                        cpal.TryGetColor(palette, layer.PaletteIndex, out color);
                    }

                    if (lines.Count < 16)
                    {
                        lines.Add(FormattableString.Invariant(
                            $"{i}: gid {layer.GlyphId}, entry {(isForeground ? "text color" : layer.PaletteIndex.ToString())} #{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"));
                    }

                    var mask = GlyphMasks.Build(typeface, scratch,
                        new GlyphMaskKey(layer.GlyphId, scaleQ, 0, GlyphMaskMode.Antialiased));

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    var cx = (i % columns) * tile + (tile - mask.Width) / 2;
                    var cy = (i / columns) * tile + (tile - mask.Height) / 2;

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

                for (var x = 0; x <= columns; x++)
                {
                    canvas.DrawLine(x * tile, 0, x * tile, rows * tile, grid);
                }

                for (var y = 0; y <= rows; y++)
                {
                    canvas.DrawLine(0, y * tile, columns * tile, y * tile, grid);
                }
            }

            if (layers.Length > 16)
            {
                lines.Add($"... {layers.Length - 16} more layers");
            }

            _layersHeader.IsVisible = true;
            _layersText.Text = string.Join(Environment.NewLine, lines);
            ReplaceImage(_layersImage, bitmap);
        }

        private static void ReplaceImage(Image target, SKBitmap bitmap)
        {
            var previous = target.Source as IDisposable;

            using (bitmap)
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
