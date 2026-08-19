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
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;

namespace TextLab
{
    /// <summary>
    /// A grid over the font's glyph-id space — monochrome glyphs through the real mask
    /// tier, color glyphs (COLR v0/v1, bitmap strikes) through their color drawings — with
    /// per-glyph capability badges (COLR color glyph, bitmap strike, missing outline) and
    /// filters over exactly those categories — the "show me every glyph that exercises
    /// pipeline X" view. Selection shows identity, metrics and Slug payload status;
    /// double-click drills into the Rasterization tab.
    /// </summary>
    public partial class GlyphExplorerView : UserControl
    {
        private const int Columns = 16;
        private const int Rows = 16;
        private const int Cell = 44;
        private const int PageSize = Columns * Rows;
        private const float CellRenderSize = 24f;

        private static readonly string[] s_filters = { "All", "Color (COLR)", "Bitmap strikes", "No outline" };

        private ComboBox _filterBox = null!;
        private TextBox _searchBox = null!;
        private Button _prevButton = null!;
        private Button _nextButton = null!;
        private TextBox _pageBox = null!;
        private TextBlock _pageCountText = null!;
        private TextBlock _summaryText = null!;
        private TextBlock _infoText = null!;
        private TextBlock _metricsText = null!;
        private TextBlock _hudText = null!;
        private CheckBox _countTiersBox = null!;
        private Button _resetCountersButton = null!;
        private Button _exportFiguresButton = null!;
        private TextBlock _exportStatusText = null!;
        private Border _emptyPanel = null!;
        private TextBlock _emptyText = null!;
        private Button _suggestFontButton = null!;
        private Image _gridImage = null!;
        private DispatcherTimer? _hudTimer;

        private GlyphTypeface? _typeface;
        private List<ushort> _glyphs = new();
        private Dictionary<ushort, List<int>>? _reverseMap;
        private int _page;
        private int _selectedIndex = -1;

        /// <summary>Raised when a cell is selected; the label carries the first mapped
        /// character when the reverse cmap knows one.</summary>
        public event Action<GlyphTypeface, ushort, string?>? GlyphSelected;

        /// <summary>Raised by the empty state's suggestion button; the host switches the
        /// app-global font selector to the named family.</summary>
        public event Action<string>? FontRequested;

        public GlyphExplorerView()
        {
            AvaloniaXamlLoader.Load(this);

            // The grid is keyboard-navigable: arrows, Home/End, PageUp/Down move the
            // selection, Enter opens the inspector.
            Focusable = true;

            _filterBox = this.FindControl<ComboBox>("FilterBox")!;
            _searchBox = this.FindControl<TextBox>("SearchBox")!;
            _prevButton = this.FindControl<Button>("PrevButton")!;
            _nextButton = this.FindControl<Button>("NextButton")!;
            _pageBox = this.FindControl<TextBox>("PageBox")!;
            _pageCountText = this.FindControl<TextBlock>("PageCountText")!;
            _summaryText = this.FindControl<TextBlock>("SummaryText")!;
            _infoText = this.FindControl<TextBlock>("InfoText")!;
            _metricsText = this.FindControl<TextBlock>("MetricsText")!;
            _hudText = this.FindControl<TextBlock>("HudText")!;
            _countTiersBox = this.FindControl<CheckBox>("CountTiersBox")!;
            _resetCountersButton = this.FindControl<Button>("ResetCountersButton")!;
            _exportFiguresButton = this.FindControl<Button>("ExportFiguresButton")!;
            _exportStatusText = this.FindControl<TextBlock>("ExportStatusText")!;
            _emptyPanel = this.FindControl<Border>("EmptyPanel")!;
            _emptyText = this.FindControl<TextBlock>("EmptyText")!;
            _suggestFontButton = this.FindControl<Button>("SuggestFontButton")!;
            _gridImage = this.FindControl<Image>("GridImage")!;

            _suggestFontButton.Click += (_, _) => FontRequested?.Invoke("Segoe UI Emoji");

            _countTiersBox.IsCheckedChanged += (_, _) =>
                Avalonia.Skia.TextTierDiagnostics.CountTiers = _countTiersBox.IsChecked == true;
            _resetCountersButton.Click += (_, _) =>
            {
                Avalonia.Skia.TextTierDiagnostics.ResetCounters();
                UpdateHud();
            };

            _filterBox.ItemsSource = s_filters;
            _filterBox.SelectedIndex = 0;
            _filterBox.SelectionChanged += (_, _) => RebuildList();
            _prevButton.Click += (_, _) => ShowPage(_page - 1);
            _nextButton.Click += (_, _) => ShowPage(_page + 1);
            _gridImage.PointerPressed += OnGridPressed;

            _searchBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Search();
                    e.Handled = true;
                }
            };

            _pageBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(_pageBox.Text, out var page))
                {
                    ShowPage(page - 1);
                    e.Handled = true;
                }
            };

            _exportFiguresButton.Click += async (_, _) => await ExportFiguresAsync();
        }

        /// <summary>Jumps to the queried glyph; widens the filter when it hides the match.</summary>
        private void Search()
        {
            if (_typeface is not { } typeface || _searchBox.Text is not { Length: > 0 } query)
            {
                return;
            }

            if (!GlyphQuery.TryResolve(typeface, query, out var glyph))
            {
                _infoText.Text = $"nothing found for \"{query}\"\n{GlyphQuery.Hint}";
                return;
            }

            var index = _glyphs.IndexOf(glyph);

            if (index < 0 && _filterBox.SelectedIndex != 0)
            {
                _filterBox.SelectedIndex = 0;   // rebuilds the list synchronously
                index = _glyphs.IndexOf(glyph);
            }

            if (index < 0)
            {
                _infoText.Text = $"glyph {glyph} is not in this font";
                return;
            }

            ShowPage(index / PageSize);
            _selectedIndex = index;
            UpdateInfo();
            RenderPage();
        }

        private async System.Threading.Tasks.Task ExportFiguresAsync()
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Export doc figures",
                AllowMultiple = false,
            });

            if (folders.Count != 1 || folders[0].TryGetLocalPath() is not { } directory)
            {
                return;
            }

            var typeface = PipelineFigures.LoadRepoInter() ?? _typeface;

            if (typeface is null)
            {
                return;
            }

            PipelineFigures.ExportAll(directory, typeface);
            _exportStatusText.Text = $"figures written to {directory}";
        }

        public void SetTypeface(GlyphTypeface? typeface)
        {
            if (ReferenceEquals(_typeface, typeface))
            {
                return;
            }

            _typeface = typeface;
            _reverseMap = null;
            _metricsText.Text = typeface is null
                ? string.Empty
                : FormattableString.Invariant(
                    $"resolved {-typeface.Metrics.Ascent}/{typeface.Metrics.Descent}/{typeface.Metrics.LineGap} of {typeface.Metrics.DesignEmHeight} upem{Environment.NewLine}{typeface.MetricsProvenance}");
            RebuildList();
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _hudTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => UpdateHud());
            _hudTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _hudTimer?.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void UpdateHud()
        {
            if (_typeface is not { } typeface)
            {
                _hudText.Text = string.Empty;
                return;
            }

            var cache = typeface.MaskCache;
            var store = typeface.SlugStore;

            var maskDraws = System.Threading.Interlocked.Read(ref Avalonia.Skia.TextTierDiagnostics.MaskTierDraws);
            var slugDraws = System.Threading.Interlocked.Read(ref Avalonia.Skia.TextTierDiagnostics.SlugTierDraws);
            var blobDraws = System.Threading.Interlocked.Read(ref Avalonia.Skia.TextTierDiagnostics.BlobTierDraws);

            _hudText.Text = FormattableString.Invariant(
                $"mask cache: {cache.Count} masks, {cache.TotalCost / 1024} KB of {GlyphMaskCache.DefaultBudgetBytes / 1024} KB{Environment.NewLine}Slug store: v{store.Version}, {store.CurveRowCount} curve + {store.BandRowCount} band rows{Environment.NewLine}tier draws: masks {maskDraws}, Slug {slugDraws}, blob {blobDraws}");
        }

        private void RebuildList()
        {
            _glyphs.Clear();
            _selectedIndex = -1;

            if (_typeface is { } typeface)
            {
                var filter = _filterBox.SelectedIndex;
                var scratch = new GlyphPathBuilder();

                for (var id = 0; id < typeface.GlyphCount && id <= ushort.MaxValue; id++)
                {
                    var glyph = (ushort)id;

                    var include = filter switch
                    {
                        1 => IsColor(typeface, glyph),
                        2 => typeface.BitmapSource?.HasGlyphImage(glyph) == true,
                        3 => !HasOutline(typeface, glyph, scratch),
                        _ => true,
                    };

                    if (include)
                    {
                        _glyphs.Add(glyph);
                    }
                }
            }

            _summaryText.Text = _typeface is null
                ? "no typeface"
                : $"{_typeface.FamilyName}: {_glyphs.Count} of {_typeface.GlyphCount} glyphs";

            // An empty filter result gets a reason and, for the color filters, a
            // color-capable system font one click away.
            var empty = _typeface is not null && _glyphs.Count == 0;

            _emptyPanel.IsVisible = empty;

            if (empty)
            {
                _emptyText.Text = _filterBox.SelectedIndex switch
                {
                    1 => $"{_typeface!.FamilyName} has no COLR color glyphs.",
                    2 => $"{_typeface!.FamilyName} has no bitmap strikes.",
                    3 => $"Every glyph in {_typeface!.FamilyName} has an outline.",
                    _ => $"{_typeface!.FamilyName} has no glyphs.",
                };
                _suggestFontButton.IsVisible = _filterBox.SelectedIndex is 1 or 2;
            }

            ShowPage(0);
        }

        private void ShowPage(int page)
        {
            var pageCount = Math.Max(1, (_glyphs.Count + PageSize - 1) / PageSize);

            _page = Math.Clamp(page, 0, pageCount - 1);
            _pageBox.Text = $"{_page + 1}";
            _pageCountText.Text = $"/ {pageCount}";
            _selectedIndex = -1;
            _infoText.Text = string.Empty;

            RenderPage();
        }

        private void RenderPage()
        {
            if (_typeface is not { } typeface)
            {
                _gridImage.Source = null;
                return;
            }

            var theme = FigureTheme.Current;
            var bitmap = new SKBitmap(new SKImageInfo(Columns * Cell + 1, Rows * Cell + 1,
                SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(bitmap))
            using (var grid = new SKPaint { Color = theme.Grid, IsStroke = true })
            using (var cellPaint = new SKPaint())
            using (var badge = new SKPaint())
            using (var label = new SKPaint { Color = new SKColor(0x90, 0x90, 0x90) })
            using (var labelFont = new SKFont(SKTypeface.Default, 9))
            using (var selection = new SKPaint { Color = theme.Hinted, IsStroke = true, StrokeWidth = 2 })
            {
                canvas.Clear(theme.Background);

                var scratch = new GlyphPathBuilder();
                var scaleQ = GlyphMaskKey.QuantizeScale(CellRenderSize);
                var first = _page * PageSize;
                var drawingOptions = new GlyphDrawingOptions { PixelSize = (int)CellRenderSize };
                var drawingScale = (double)CellRenderSize / typeface.Metrics.DesignEmHeight;

                // Ink pass: color glyphs render through their color drawing (the same
                // IGlyphDrawing path the renderer splits them onto), everything else through
                // the mono mask tier. The DrawingContext wrapper is disposed before the
                // chrome pass so the raw canvas state is clean for labels and badges.
                using (var contextImpl = Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(
                           canvas, new Vector(96, 96)))
                using (var drawContext = new PlatformDrawingContext(contextImpl, ownsImpl: false))
                {
                    for (var i = 0; i < PageSize && first + i < _glyphs.Count; i++)
                    {
                        var glyph = _glyphs[first + i];
                        var cx = (i % Columns) * Cell;
                        var cy = (i / Columns) * Cell;

                        if (typeface.GetGlyphDrawing(glyph, drawingOptions) is { } drawing &&
                            drawing.Bounds is { Width: > 0, Height: > 0 } bounds)
                        {
                            var fit = drawingScale;

                            if (bounds.Width * fit > Cell - 4)
                            {
                                fit = (Cell - 4) / bounds.Width;
                            }

                            if (bounds.Height * fit > Cell - 13)
                            {
                                fit = Math.Min(fit, (Cell - 13) / bounds.Height);
                            }

                            var penX = cx + (Cell - bounds.Width * fit) / 2 - bounds.X * fit;
                            var penY = cy + (Cell - 11 - bounds.Height * fit) / 2 - bounds.Y * fit;

                            using (drawContext.PushTransform(
                                       Matrix.CreateScale(fit, fit) * Matrix.CreateTranslation(penX, penY)))
                            {
                                drawing.Draw(drawContext, default);
                            }

                            continue;
                        }

                        var mask = GlyphMasks.Build(typeface, scratch,
                            new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased));

                        if (!mask.IsEmpty)
                        {
                            // Center the ink in the cell's upper region; the label owns the bottom.
                            var originX = cx + (Cell - mask.Width) / 2;
                            var originY = cy + (Cell - 11 - mask.Height) / 2;

                            for (var y = 0; y < mask.Height; y++)
                            {
                                for (var x = 0; x < mask.Width; x++)
                                {
                                    var coverage = mask.Alpha[y * mask.Width + x];

                                    if (coverage > 0)
                                    {
                                        cellPaint.Color = theme.Ink.WithAlpha(coverage);
                                        canvas.DrawRect(originX + x, originY + y, 1, 1, cellPaint);
                                    }
                                }
                            }
                        }
                    }
                }

                // Chrome pass: labels, badges and selection on the raw canvas.
                for (var i = 0; i < PageSize && first + i < _glyphs.Count; i++)
                {
                    var glyph = _glyphs[first + i];
                    var cx = (i % Columns) * Cell;
                    var cy = (i / Columns) * Cell;

                    canvas.DrawText($"{glyph}", cx + 3, cy + Cell - 3, SKTextAlign.Left, labelFont, label);

                    if (IsColor(typeface, glyph))
                    {
                        badge.Color = new SKColor(0xCC, 0x22, 0x99);
                        canvas.DrawCircle(cx + Cell - 6, cy + 6, 3, badge);
                    }

                    if (typeface.BitmapSource?.HasGlyphImage(glyph) == true)
                    {
                        badge.Color = new SKColor(0xDD, 0x88, 0x22);
                        canvas.DrawCircle(cx + Cell - 6, cy + 14, 3, badge);
                    }

                    if (!HasOutline(typeface, glyph, scratch))
                    {
                        badge.Color = new SKColor(0xD4, 0x33, 0x22, 0x80);
                        badge.IsStroke = true;
                        canvas.DrawRect(cx + 1, cy + 1, Cell - 2, Cell - 2, badge);
                        badge.IsStroke = false;
                    }

                    if (first + i == _selectedIndex)
                    {
                        canvas.DrawRect(cx + 1, cy + 1, Cell - 2, Cell - 2, selection);
                    }
                }

                for (var x = 0; x <= Columns; x++)
                {
                    canvas.DrawLine(x * Cell, 0, x * Cell, Rows * Cell, grid);
                }

                for (var y = 0; y <= Rows; y++)
                {
                    canvas.DrawLine(0, y * Cell, Columns * Cell, y * Cell, grid);
                }
            }

            var previous = _gridImage.Source as IDisposable;

            using (bitmap)
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                _gridImage.Source = new Bitmap(stream);
            }

            previous?.Dispose();
        }

        private void OnGridPressed(object? sender, PointerPressedEventArgs e)
        {
            Focus();

            var position = e.GetPosition(_gridImage);
            var column = (int)(position.X / Cell);
            var row = (int)(position.Y / Cell);

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
            RaiseSelected();
        }

        /// <summary>Repaints the current page - the host calls this on theme changes,
        /// which figure bitmaps cannot follow by themselves.</summary>
        public void Repaint() => RenderPage();

        private void RaiseSelected()
        {
            if (_typeface is { } typeface && _selectedIndex >= 0 && _selectedIndex < _glyphs.Count)
            {
                var glyph = _glyphs[_selectedIndex];
                var codepoints = GetCodepoints(typeface, glyph);
                var label = codepoints.Count > 0 && codepoints[0] is >= 0x20
                    ? $"'{char.ConvertFromUtf32(codepoints[0])}'"
                    : null;

                GlyphSelected?.Invoke(typeface, glyph, label);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Text boxes own their keys; the grid reacts only when the view has focus.
            if (e.Handled || e.Source is TextBox || _typeface is null || _glyphs.Count == 0)
            {
                base.OnKeyDown(e);
                return;
            }

            var current = _selectedIndex >= 0 ? _selectedIndex : _page * PageSize;
            int? target = e.Key switch
            {
                Key.Left => current - 1,
                Key.Right => current + 1,
                Key.Up => current - Columns,
                Key.Down => current + Columns,
                Key.PageUp => current - PageSize,
                Key.PageDown => current + PageSize,
                Key.Home => 0,
                Key.End => _glyphs.Count - 1,
                _ => null,
            };

            if (target is { } index)
            {
                index = Math.Clamp(index, 0, _glyphs.Count - 1);

                if (index / PageSize != _page)
                {
                    ShowPage(index / PageSize);
                }

                _selectedIndex = index;
                UpdateInfo();
                RenderPage();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _selectedIndex >= 0)
            {
                RaiseSelected();
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        private void UpdateInfo()
        {
            if (_typeface is not { } typeface || _selectedIndex < 0 || _selectedIndex >= _glyphs.Count)
            {
                _infoText.Text = string.Empty;
                return;
            }

            var glyph = _glyphs[_selectedIndex];
            var scratch = new GlyphPathBuilder();
            var lines = new List<string> { $"glyph id: {glyph}" };

            var codepoints = GetCodepoints(typeface, glyph);

            lines.Add(codepoints.Count == 0
                ? "codepoints: none (GID-only: ligature, alternate or component)"
                : "codepoints: " + string.Join(" ", codepoints.ConvertAll(c =>
                    $"U+{c:X4}{(c is >= 0x20 and < 0x10000 ? $" '{char.ConvertFromUtf32(c)}'" : "")}")));

            if (typeface.TryGetGlyphMetrics(glyph, out var metrics))
            {
                lines.Add($"advance: {metrics.AdvanceWidth} design units");
            }

            if (typeface.TryGetGlyphInkBounds(glyph, out var box))
            {
                lines.Add($"ink box: x {box.XMin}..{box.XMax}, y {box.YMin}..{box.YMax}");
            }

            lines.Add($"outline: {(HasOutline(typeface, glyph, scratch) ? "yes" : "no")}");

            if (typeface.ColorTable is { } colr)
            {
                var v0 = colr.HasColorLayers(glyph);
                var v1 = colr.TryGetBaseGlyphV1Record(glyph, out _);

                if (v0 || v1)
                {
                    lines.Add($"COLR: {(v1 ? "v1 paint graph" : "v0 layers")}");
                }
            }

            if (typeface.BitmapSource?.HasGlyphImage(glyph) == true)
            {
                lines.Add("bitmap strike: yes");
            }

            lines.Add(typeface.SlugStore.TryRealize(typeface, glyph, out _)
                ? "Slug payload: ok"
                : "Slug payload: declined (caps exceeded or unwalkable)");

            _infoText.Text = string.Join(Environment.NewLine, lines);
        }

        /// <summary>Reverse cmap, built once per typeface from the map's own enumerator.</summary>
        private List<int> GetCodepoints(GlyphTypeface typeface, ushort glyph)
        {
            if (_reverseMap is null)
            {
                _reverseMap = new Dictionary<ushort, List<int>>();

                foreach (var pair in new Avalonia.Media.Fonts.Tables.Cmap.CharacterToGlyphMapDictionary(
                             typeface.CharacterToGlyphMap))
                {
                    if (!_reverseMap.TryGetValue(pair.Value, out var list))
                    {
                        _reverseMap[pair.Value] = list = new List<int>();
                    }

                    if (list.Count < 8)
                    {
                        list.Add(pair.Key);
                    }
                }
            }

            return _reverseMap.TryGetValue(glyph, out var result) ? result : new List<int>();
        }

        private static bool IsColor(GlyphTypeface typeface, ushort glyph)
            => typeface.ColorTable is { } colr &&
               (colr.HasColorLayers(glyph) || colr.TryGetBaseGlyphV1Record(glyph, out _));

        private static bool HasOutline(GlyphTypeface typeface, ushort glyph, GlyphPathBuilder scratch)
        {
            scratch.Reset();

            return typeface.TryBuildGlyphContours(glyph, Matrix.Identity, scratch) &&
                   scratch.Verbs.Length > 0;
        }
    }
}
