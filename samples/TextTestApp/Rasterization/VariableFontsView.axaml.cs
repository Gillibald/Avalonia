using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// The variable fonts explorer: axis sliders and named-instance presets build the
    /// normalized position through <c>CreateNormalizedPosition</c> (fvar/avar-aware; the
    /// readout shows user -> normalized per axis and flags avar remapping), the specimen
    /// and an interpolation ladder render through <c>WithVariation</c> clones on the real
    /// managed pipeline, and the diagnostics block states how this instance hints plus a
    /// default-vs-instance outline overlay.
    /// </summary>
    public partial class VariableFontsView : UserControl
    {
        private const int LadderSteps = 7;

        private TextBox _sampleBox = null!;
        private ComboBox _ladderAxisBox = null!;
        private Button _copyButton = null!;
        private TextBlock _fontText = null!;
        private StackPanel _axesPanel = null!;
        private Button _resetButton = null!;
        private WrapPanel _instancePanel = null!;
        private TextBlock _coordsText = null!;
        private TextBlock _matchText = null!;
        private Image _specimenImage = null!;
        private TextBlock _ladderTitle = null!;
        private Image _ladderImage = null!;
        private TextBlock _engineText = null!;
        private TextBlock _cacheText = null!;
        private Image _overlayImage = null!;
        private TextBlock _overlayTitle = null!;
        private Border _emptyPanel = null!;
        private TextBlock _emptyText = null!;
        private StackPanel _suggestPanel = null!;

        private GlyphTypeface? _source;
        private double _size = 13;
        private IReadOnlyList<FontVariationAxis> _axes = Array.Empty<FontVariationAxis>();
        private readonly Dictionary<OpenTypeTag, float> _userValues = new();
        private readonly List<(FontVariationAxis Axis, Slider Slider, TextBox Box)> _axisRows = new();
        private readonly DispatcherTimer _rebuildTimer;
        private bool _updatingUi;

        /// <summary>Raised by the empty state's suggestion buttons; the host switches the
        /// app-global font selector to the named family.</summary>
        public event Action<string>? FontRequested;

        private sealed class AxisItem
        {
            public AxisItem(FontVariationAxis axis) => Axis = axis;
            public FontVariationAxis Axis { get; }
            public override string ToString() => $"{Axis.Tag} - {Axis.Name}";
        }

        public VariableFontsView()
        {
            AvaloniaXamlLoader.Load(this);

            _sampleBox = this.FindControl<TextBox>("SampleBox")!;
            _ladderAxisBox = this.FindControl<ComboBox>("LadderAxisBox")!;
            _copyButton = this.FindControl<Button>("CopyButton")!;
            _fontText = this.FindControl<TextBlock>("FontText")!;
            _axesPanel = this.FindControl<StackPanel>("AxesPanel")!;
            _resetButton = this.FindControl<Button>("ResetButton")!;
            _instancePanel = this.FindControl<WrapPanel>("InstancePanel")!;
            _coordsText = this.FindControl<TextBlock>("CoordsText")!;
            _matchText = this.FindControl<TextBlock>("MatchText")!;
            _specimenImage = this.FindControl<Image>("SpecimenImage")!;
            _ladderTitle = this.FindControl<TextBlock>("LadderTitle")!;
            _ladderImage = this.FindControl<Image>("LadderImage")!;
            _engineText = this.FindControl<TextBlock>("EngineText")!;
            _cacheText = this.FindControl<TextBlock>("CacheText")!;
            _overlayImage = this.FindControl<Image>("OverlayImage")!;
            _overlayTitle = this.FindControl<TextBlock>("OverlayTitle")!;
            _emptyPanel = this.FindControl<Border>("EmptyPanel")!;
            _emptyText = this.FindControl<TextBlock>("EmptyText")!;
            _suggestPanel = this.FindControl<StackPanel>("SuggestPanel")!;

            // Slider drags fire per pixel; one short timer coalesces them into a rebuild.
            _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _rebuildTimer.Tick += (_, _) =>
            {
                _rebuildTimer.Stop();
                RebuildRenders();
            };

            _sampleBox.TextChanged += (_, _) => ScheduleRebuild();
            _ladderAxisBox.SelectionChanged += (_, _) => ScheduleRebuild();
            _resetButton.Click += (_, _) => ApplyValues(null);
            _copyButton.Click += (_, _) => ClipboardHelper.Copy(this, string.Join(Environment.NewLine,
                _fontText.Text, _coordsText.Text, _matchText.Text, _engineText.Text));
        }

        /// <summary>The app-global font and size; a typeface change rebuilds the axis UI.</summary>
        public void SetContext(GlyphTypeface? typeface, double size)
        {
            _size = size;

            if (!ReferenceEquals(_source, typeface))
            {
                _source = typeface;
                _axes = typeface?.VariationAxes ?? Array.Empty<FontVariationAxis>();
                BuildAxisUi();
            }

            _fontText.Text = typeface is null
                ? null
                : FormattableString.Invariant($"{typeface.FamilyName}, {Math.Clamp(size, 6, 200):0.#} px");

            ScheduleRebuild();
        }

        /// <summary>Repaints the renders - the host calls this on theme changes, which
        /// figure bitmaps cannot follow by themselves.</summary>
        public void Repaint() => RebuildRenders();

        private void ScheduleRebuild()
        {
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void BuildAxisUi()
        {
            _axesPanel.Children.Clear();
            _instancePanel.Children.Clear();
            _axisRows.Clear();
            _userValues.Clear();

            var empty = _source is null || _axes.Count == 0;

            _emptyPanel.IsVisible = empty;

            if (empty)
            {
                if (_source is not null)
                {
                    _emptyText.Text = $"{_source.FamilyName} has no variation axes.";
                }

                BuildSuggestions();
                _ladderAxisBox.ItemsSource = null;
                return;
            }

            _updatingUi = true;

            foreach (var axis in _axes)
            {
                _userValues[axis.Tag] = axis.DefaultValue;

                var hidden = axis.IsHidden ? " (hidden)" : "";
                var header = new DockPanel();

                var range = new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Text = FormattableString.Invariant(
                        $"{Fmt.N(axis.MinimumValue)} .. {Fmt.N(axis.DefaultValue)} .. {Fmt.N(axis.MaximumValue)}"),
                };
                DockPanel.SetDock(range, Dock.Right);
                header.Children.Add(range);
                header.Children.Add(new TextBlock { FontSize = 12, Text = $"{axis.Tag} - {axis.Name}{hidden}" });

                var slider = new Slider
                {
                    Minimum = axis.MinimumValue,
                    Maximum = axis.MaximumValue,
                    Value = axis.DefaultValue,
                    SmallChange = Math.Max((axis.MaximumValue - axis.MinimumValue) / 100.0, 0.01),
                };
                AutomationProperties.SetName(slider, $"{axis.Name} axis");

                var valueBox = new TextBox { Width = 64, FontSize = 12, Text = Fmt.N(axis.DefaultValue) };
                AutomationProperties.SetName(valueBox, $"{axis.Name} value");
                DockPanel.SetDock(valueBox, Dock.Right);

                var row = new DockPanel();
                row.Children.Add(valueBox);
                row.Children.Add(slider);

                var capturedAxis = axis;

                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == RangeBase.ValueProperty && !_updatingUi)
                    {
                        var value = (float)slider.Value;

                        _userValues[capturedAxis.Tag] = value;
                        valueBox.Text = Fmt.N(value);
                        ScheduleRebuild();
                    }
                };

                valueBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter &&
                        float.TryParse(valueBox.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var typed))
                    {
                        slider.Value = Math.Clamp(typed, capturedAxis.MinimumValue, capturedAxis.MaximumValue);
                        e.Handled = true;
                    }
                };

                _axisRows.Add((axis, slider, valueBox));
                _axesPanel.Children.Add(new StackPanel { Spacing = 2, Children = { header, row } });
            }

            _updatingUi = false;

            var instances = _source!.NamedInstances;

            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                var chip = new Button
                {
                    Content = instance.Name is { Length: > 0 } name ? name : $"instance {i}",
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 6),
                };

                chip.Click += (_, _) => ApplyValues(instance.Coordinates);
                _instancePanel.Children.Add(chip);
            }

            if (instances.Count == 0)
            {
                _instancePanel.Children.Add(new TextBlock
                {
                    Text = "none declared", FontSize = 12, Opacity = 0.6,
                });
            }

            var items = new List<AxisItem>();

            foreach (var axis in _axes)
            {
                items.Add(new AxisItem(axis));
            }

            _ladderAxisBox.ItemsSource = items;
            _ladderAxisBox.SelectedIndex = 0;
        }

        /// <summary>Sets every axis from the given user-space coordinates (defaults when
        /// null or missing) and rebuilds - the named-instance and reset path.</summary>
        private void ApplyValues(IReadOnlyDictionary<OpenTypeTag, float>? coordinates)
        {
            _updatingUi = true;

            foreach (var (axis, slider, box) in _axisRows)
            {
                var value = axis.DefaultValue;

                if (coordinates is not null && coordinates.TryGetValue(axis.Tag, out var supplied))
                {
                    value = Math.Clamp(supplied, axis.MinimumValue, axis.MaximumValue);
                }

                _userValues[axis.Tag] = value;
                slider.Value = value;
                box.Text = Fmt.N(value);
            }

            _updatingUi = false;
            ScheduleRebuild();
        }

        private void RebuildRenders()
        {
            if (_source is not { } source || _axes.Count == 0 ||
                _sampleBox.Text is not { Length: > 0 } sample)
            {
                return;
            }

            var size = (float)Math.Clamp(_size, 6, 200);
            var position = source.CreateNormalizedPosition(ToSettings(_userValues));
            var clone = source.WithVariation(position);

            SetImage(_specimenImage, RenderRun(clone, sample, size));
            RenderLadder(source, sample);
            RenderOverlay(source, clone, sample);
            UpdateCoordinates(source, position);
            UpdateDiagnostics(clone, sample, size);
        }

        private void RenderLadder(GlyphTypeface source, string sample)
        {
            if (_ladderAxisBox.SelectedItem is not AxisItem item)
            {
                _ladderImage.Source = null;
                return;
            }

            var axis = item.Axis;
            var t = FigureTheme.Current;
            var rowSize = (float)Math.Clamp(Math.Min(_size, 30), 12, 30);
            var rows = new (string Label, SKBitmap Bitmap)[LadderSteps];
            var maxWidth = 0;
            var totalHeight = 0;

            _ladderTitle.Text = $"Interpolation ladder - {axis.Tag} ({axis.Name})";

            for (var i = 0; i < LadderSteps; i++)
            {
                var value = axis.MinimumValue + (axis.MaximumValue - axis.MinimumValue) * i / (LadderSteps - 1);
                var coords = new Dictionary<OpenTypeTag, float>(_userValues) { [axis.Tag] = value };
                var stepClone = source.WithVariation(source.CreateNormalizedPosition(ToSettings(coords)));
                var bitmap = RenderRun(stepClone, sample, rowSize);

                rows[i] = (Fmt.N(value), bitmap);
                maxWidth = Math.Max(maxWidth, bitmap.Width);
                totalHeight += bitmap.Height + 2;
            }

            const int labelWidth = 64;
            var composed = new SKBitmap(new SKImageInfo(labelWidth + maxWidth, totalHeight,
                SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(composed))
            using (var font = new SKFont(SKTypeface.Default, 12))
            using (var label = new SKPaint { Color = t.Faint })
            {
                canvas.Clear(t.Background);

                var y = 0;

                foreach (var (text, bitmap) in rows)
                {
                    using (var image = SKImage.FromBitmap(bitmap))
                    {
                        canvas.DrawText(text, 6, y + bitmap.Height * 0.72f, SKTextAlign.Left, font, label);
                        canvas.DrawImage(image, labelWidth, y);
                    }

                    y += bitmap.Height + 2;
                    bitmap.Dispose();
                }
            }

            SetImage(_ladderImage, composed);
        }

        private void RenderOverlay(GlyphTypeface source, GlyphTypeface clone, string sample)
        {
            var glyph = PickOverlayGlyph(source, sample);
            var t = FigureTheme.Current;
            var upem = source.Metrics.DesignEmHeight;
            var scale = 200f / upem;

            source.TryGetGlyphMetrics(glyph, out var metrics);

            var baselineY = 14 + (float)(-source.Metrics.Ascent) * scale;
            var width = Math.Max((int)Math.Ceiling(metrics.AdvanceWidth * scale) + 56, 220);
            var height = (int)Math.Ceiling(baselineY + (float)source.Metrics.Descent * scale) + 18;
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(t.Background);

                float MapX(float x) => 28 + x;
                float MapY(float y) => baselineY + y;

                var defaultOutline = new Avalonia.Media.Fonts.Rasterization.GlyphPathBuilder();
                var instanceOutline = new Avalonia.Media.Fonts.Rasterization.GlyphPathBuilder();

                source.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), defaultOutline);
                clone.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, -scale, 0, 0), instanceOutline);

                PipelineFigures.DrawOutline(canvas, defaultOutline, MapX, MapY, t.Unhinted);
                PipelineFigures.DrawOutline(canvas, instanceOutline, MapX, MapY, t.Hinted);
            }

            var label = glyph == 0 ? "" : $" - '{sample[0]}'";

            _overlayTitle.Text = $"Outline overlay{label}: default instance red, current instance blue";
            SetImage(_overlayImage, bitmap);
        }

        private static ushort PickOverlayGlyph(GlyphTypeface typeface, string sample)
        {
            foreach (var c in sample)
            {
                if (typeface.CharacterToGlyphMap.ContainsGlyph(c))
                {
                    return typeface.CharacterToGlyphMap[c];
                }
            }

            return typeface.CharacterToGlyphMap.ContainsGlyph('g') ? typeface.CharacterToGlyphMap['g'] : (ushort)0;
        }

        private void UpdateCoordinates(GlyphTypeface source, NormalizedVariationPosition position)
        {
            var report = new StringBuilder();

            foreach (var axis in _axes)
            {
                var user = _userValues.TryGetValue(axis.Tag, out var v) ? v : axis.DefaultValue;
                var normalized = position.GetCoordinateOrDefault(axis.Tag);
                var linear = LinearNormalize(axis, user);
                var note = Math.Abs(normalized - linear) > 0.0005f ? " (avar)"
                    : normalized == 0f ? " (default)"
                    : "";

                report.AppendLine(FormattableString.Invariant(
                    $"{axis.Tag} {Fmt.N(user)} -> {normalized:0.####}{note}"));
            }

            _coordsText.Text = report.ToString().TrimEnd();

            string? match = null;
            var instances = source.NamedInstances;

            for (var i = 0; i < instances.Count; i++)
            {
                if (source.CreateNormalizedPosition(null, instances[i].Index) == position)
                {
                    match = instances[i].Name;
                    break;
                }
            }

            _matchText.Text = match is { Length: > 0 }
                ? $"matches named instance \"{match}\""
                : "no named instance at this point";
        }

        /// <summary>Converts the slider dictionary into user-space settings for normalization.</summary>
        private static FontVariationSettings ToSettings(Dictionary<OpenTypeTag, float> values)
        {
            var variations = new FontVariation[values.Count];
            var i = 0;

            foreach (var pair in values)
            {
                variations[i++] = new FontVariation(pair.Key, pair.Value);
            }

            return new FontVariationSettings(variations);
        }

        /// <summary>The pre-avar half of CreateNormalizedPosition, for flagging avar remaps.</summary>
        private static float LinearNormalize(FontVariationAxis axis, float user)
        {
            user = Math.Clamp(user, axis.MinimumValue, axis.MaximumValue);

            if (user == axis.DefaultValue)
            {
                return 0f;
            }

            if (user < axis.DefaultValue)
            {
                var range = axis.DefaultValue - axis.MinimumValue;
                return range > 0f ? (user - axis.DefaultValue) / range : 0f;
            }

            var above = axis.MaximumValue - axis.DefaultValue;
            return above > 0f ? (user - axis.DefaultValue) / above : 0f;
        }

        private void UpdateDiagnostics(GlyphTypeface clone, string sample, float size)
        {
            var glyph = PickOverlayGlyph(clone, sample);
            var probe = TrueTypeHintingProbe.TryCreate(clone, glyph, size,
                Avalonia.Media.Fonts.Rasterization.GlyphMaskMode.Antialiased,
                stemSnap: false, out var engineNote);

            if (probe is { } p)
            {
                var interpretation = p.FullInterpretation ? "full interpretation" : "v40 class (y only)";

                _engineText.Text = FormattableString.Invariant(
                    $"engine: TrueType bytecode at this instance, {interpretation}, {p.InstructionsExecuted} ops - the CVT the programs read carries the cvar deltas for this variation point");
            }
            else
            {
                _engineText.Text = clone.HasTrueTypeHinting
                    ? $"engine: auto-hinter ({engineNote ?? "bytecode unavailable"})"
                    : "engine: auto-hinter (the font ships no hinting machinery)";
            }

            _cacheText.Text = FormattableString.Invariant(
                $"instance clones are cached on the source typeface; this clone's mask cache holds {clone.MaskCache.Count} masks ({clone.MaskCache.TotalCost / 1024} KB) - every instance caches separately because its masks differ");
        }

        /// <summary>One line of the sample through the real pipeline (subpixel on an
        /// RGB-striped surface) with the given typeface instance.</summary>
        private static SKBitmap RenderRun(GlyphTypeface typeface, string text, float size)
        {
            var t = FigureTheme.Current;
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

            var width = Math.Max((int)Math.Ceiling(advance) + 20, 40);
            var height = (int)Math.Ceiling(size * 1.5) + 6;
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
                    TextHintingMode = TextHintingMode.Light,
                });

                using var run = PipelineFigures.CreateRun(typeface, text, size, new Point(10.3, size * 1.15));

                context.DrawGlyphRun(inkBrush, run);
                context.PopTextOptions();
            }

            using var snapshot = surface.Snapshot();
            var bitmap = new SKBitmap(info);

            snapshot.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            return bitmap;
        }

        private void BuildSuggestions()
        {
            _suggestPanel.Children.Clear();

            // Prefix match: Windows 11 enumerates Segoe UI Variable as separate
            // Display/Text/Small families rather than one umbrella name.
            foreach (var prefix in new[] { "Segoe UI Variable", "Bahnschrift" })
            {
                foreach (var family in FontManager.Current.SystemFonts)
                {
                    if (family.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = family.Name;
                        var button = new Button { Content = $"Switch to {name}", FontSize = 12 };

                        button.Click += (_, _) => FontRequested?.Invoke(name);
                        _suggestPanel.Children.Add(button);
                        break;
                    }
                }
            }
        }

        private static void SetImage(Image target, SKBitmap bitmap)
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
