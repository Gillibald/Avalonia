using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour.Chapters
{
    /// <summary>
    /// The string broken into lines at an adjustable width: break opportunities and the chosen
    /// break per line, runs moving from logical into visual order, line metrics, a font with other
    /// vertical metrics, and trimming.
    /// </summary>
    internal sealed class LayoutChapter : Chapter
    {
        /// <summary>0 draws each line's runs in logical order, 1 in visual order.</summary>
        public static readonly StyledProperty<double> VisualMixProperty =
            AvaloniaProperty.Register<LayoutChapter, double>(nameof(VisualMix));

        private const double FontSize = 64;

        private readonly Slider _slider;
        private readonly TextBlock _sliderLabel;
        private readonly List<TextLayout> _layouts = new();
        private TextLayout? _layout;
        private TextLayout? _alternate;
        private string? _alternateFamily;
        private Point _origin;
        private bool _dragging;
        private bool _syncingSlider;
        private int _hoverLine = -1;

        static LayoutChapter()
        {
            AffectsRender<LayoutChapter>(VisualMixProperty);
        }

        public LayoutChapter(TourState state) : base(state)
        {
            _slider = new Slider
            {
                Minimum = 0.12,
                Maximum = 1,
                Value = state.WrapFraction,
                Focusable = false,
                Width = 360,
            };
            _slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !_syncingSlider)
                {
                    State.WrapFraction = _slider.Value;
                }
            };

            _sliderLabel = new TextBlock { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(Pad, 12, 0, 0),
            };
            bar.Children.Add(new TextBlock { Text = "Width", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_slider);
            bar.Children.Add(_sliderLabel);

            Child = bar;
        }

        public override string Title => "Layout";

        public override int StepCount => 6;

        public double VisualMix
        {
            get => GetValue(VisualMixProperty);
            set => SetValue(VisualMixProperty, value);
        }

        public override string StepName => Step switch
        {
            1 => "lines at a width",
            2 => "break opportunities",
            3 => VisualMix < 0.5 ? "logical order (V)" : "visual order (V)",
            4 => "line metrics",
            5 => "another font",
            _ => "trimming",
        };

        public override string Caption => Step switch
        {
            1 => $"new TextLayout(..., TextWrapping.Wrap, maxWidth: {WrapWidth():0})  ->  TextLayout.TextLines",
            2 => "LineBreakEnumerator  ->  TextLine.Length, TextLine.TextLineBreak",
            3 => "TextLine.TextRuns (runs are reordered into visual order when the line is finalized)",
            4 => "TextLine.Baseline, .Height, .Extent; GlyphTypeface.Metrics.Ascent, .Descent, .LineGap",
            5 => "GlyphTypeface.Metrics: each font brings its own ascent, descent and line gap",
            _ => "TextTrimming.CharacterEllipsis, .WordEllipsis, .PrefixCharacterEllipsis; maxLines",
        };

        public override int ExportVariantCount(int step) => step == 3 ? 2 : 1;

        public override string ExportVariantName(int step, int variant) => step == 3 ? variant == 0 ? "-logical" : "-visual" : string.Empty;

        public override void PrepareExport(int step, int variant)
        {
            SetCurrentValue(VisualMixProperty, step == 3 && variant == 1 ? 1.0 : 0.0);
            _hoverLine = step == 4 ? 0 : -1;
            RaiseCaptionChanged();
            InvalidateVisual();
        }

        public override bool HandleKey(Key key, KeyModifiers modifiers)
        {
            if (Step == 3 && key == Key.V)
            {
                UpdateMixTransition();
                SetCurrentValue(VisualMixProperty, VisualMix < 0.5 ? 1.0 : 0.0);
                RaiseCaptionChanged();
                return true;
            }

            return false;
        }

        protected override void OnStepChanged()
        {
            if (Step != 3)
            {
                SetCurrentValue(VisualMixProperty, 0.0);
            }

            // Steps 5 and 6 lay out their own samples.
            InvalidateModel();
        }

        protected override void OnStateChanged(TourChange change)
        {
            if ((change & TourChange.Width) != 0 && Math.Abs(_slider.Value - State.WrapFraction) > 0.0001)
            {
                _syncingSlider = true;
                _slider.Value = State.WrapFraction;
                _syncingSlider = false;
            }

            base.OnStateChanged(change);
        }

        protected override void InvalidateModel()
        {
            DisposeLayouts();
            _sliderLabel.Text = $"{WrapWidth():0} px";
            _sliderLabel.Foreground = Palette.Foreground;
            base.InvalidateModel();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            EnsureLayout();

            if (Math.Abs(e.GetPosition(this).X - (_origin.X + WrapWidth())) < 16)
            {
                _dragging = true;
                e.Pointer.Capture(this);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);

            if (_dragging)
            {
                State.WrapFraction = (point.X - _origin.X) / ContentWidth();
                return;
            }

            Cursor = Math.Abs(point.X - (_origin.X + WrapWidth())) < 16 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;

            if (Step == 4 && _layout is not null)
            {
                var line = LineAt(point.Y - _origin.Y);

                if (line != _hoverLine)
                {
                    _hoverLine = line;
                    InvalidateVisual();
                }
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = false;
            e.Pointer.Capture(null);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            EnsureLayout();

            var p = Palette;

            switch (Step)
            {
                case 1 or 2:
                    RenderWrapped(context, Step == 2);
                    break;
                case 3:
                    RenderOrder(context);
                    break;
                case 4:
                    RenderMetrics(context, _layout!, _origin, true);
                    break;
                case 5:
                    RenderFontSwitch(context);
                    break;
                default:
                    RenderTrimming(context);
                    break;
            }
        }

        private void RenderWidthGuide(DrawingContext context, double height)
        {
            var p = Palette;
            var x = _origin.X + WrapWidth();
            context.DrawLine(Draw.Pen(p.Accent, 2, dashed: true), new Point(x, _origin.Y - 16), new Point(x, _origin.Y + height + 16));
            var grip = new Rect(x - 6, _origin.Y + height / 2 - 24, 12, 48);
            context.DrawRectangle(p.Accent, null, grip, 4, 4);
        }

        private void RenderWrapped(DrawingContext context, bool breaks)
        {
            var p = Palette;
            var layout = _layout!;
            RenderWidthGuide(context, layout.Height);
            layout.Draw(context, _origin);

            if (!breaks)
            {
                return;
            }

            var analysis = State.Analysis;
            var top = 0.0;

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var start = line.FirstTextSourceIndex;
                var end = start + line.Length;
                var bottom = _origin.Y + top + line.Height;

                foreach (var lineBreak in analysis.Breaks)
                {
                    var position = lineBreak.PositionWrap;

                    if (position <= start || position > end)
                    {
                        continue;
                    }

                    var chosen = position == end && l < layout.TextLines.Count - 1;
                    var x = _origin.X + (position == end ?
                        line.GetDistanceFromCharacterHit(new CharacterHit(position - 1, 1)) :
                        line.GetDistanceFromCharacterHit(new CharacterHit(position)));

                    var pen = chosen ? Draw.Pen(p.Warning, 5) :
                        lineBreak.Required ? Draw.Pen(p.Warning, 3) : Draw.Pen(p.Accent, 2.5, dashed: true);
                    context.DrawLine(pen, new Point(x, _origin.Y + top + 2), new Point(x, bottom - 2));

                    if (chosen)
                    {
                        Draw.Text(context, "break", x, _origin.Y + top - 18, 16, p.Warning, HAlign.Center, Draw.UiBold);
                    }
                }

                Draw.Text(context, $"line {l}: {start}..{end - 1}, Length {line.Length}", _origin.X + WrapWidth() + 24,
                    _origin.Y + top + line.Height / 2 - 12, 18, p.Muted);
                top += line.Height;
            }
        }

        private void RenderOrder(DrawingContext context)
        {
            var p = Palette;
            var layout = _layout!;
            var mix = VisualMix;
            var top = 0.0;

            RenderWidthGuide(context, layout.Height);

            foreach (var line in layout.TextLines)
            {
                var runs = line.TextRuns.OfType<DrawableTextRun>().ToList();
                var logical = runs.OrderBy(StartOf).ToList();
                var logicalX = new Dictionary<TextRun, double>();
                var visualX = new Dictionary<TextRun, double>();

                var x = line.Start;

                foreach (var run in logical)
                {
                    logicalX[run] = x;
                    x += run.Size.Width;
                }

                x = line.Start;

                foreach (var run in runs)
                {
                    visualX[run] = x;
                    x += run.Size.Width;
                }

                foreach (var run in runs)
                {
                    var rx = _origin.X + logicalX[run] + (visualX[run] - logicalX[run]) * mix;
                    var level = run is ShapedTextRun s ? s.BidiLevel : 0;
                    var box = new Rect(rx, _origin.Y + top, run.Size.Width, line.Height);
                    context.DrawRectangle(level % 2 == 1 ? p.SeriesFill(3) : level > 0 ? p.SeriesFill(4) : null,
                        Draw.Pen(level == 0 ? p.Grid : p.Series(level + 2), 1.5), box.Deflate(new Thickness(1, 2)), 4, 4);
                    run.Draw(context, new Point(rx, _origin.Y + top + TextLineImpl.GetBaselineOffset(line, run)));
                    Draw.Text(context, $"{StartOf(run)}", rx + 4, box.Bottom - 20, 14, p.Muted);
                }

                top += line.Height;
            }

            Draw.Text(context, mix < 0.5 ?
                    "runs in logical order (as the text source handed them out); press V" :
                    "runs in visual order (as the line draws them); press V",
                _origin.X, _origin.Y + top + 24, 20, p.Muted);
        }

        private void RenderMetrics(DrawingContext context, TextLayout layout, Point origin, bool labels)
        {
            var p = Palette;
            var top = 0.0;
            var face = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().FirstOrDefault()?.ShapedBuffer.GlyphTypeface;

            layout.Draw(context, origin);

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var y0 = origin.Y + top;
                var baseline = y0 + line.Baseline;
                var x0 = origin.X - 8;
                var x1 = origin.X + Math.Max(line.WidthIncludingTrailingWhitespace, 40) + 8;

                context.DrawRectangle(null, Draw.Pen(p.Grid, 1), new Rect(origin.X, y0, layout.MaxWidth < 1e6 ? layout.MaxWidth : x1 - origin.X, line.Height));

                if (face is not null)
                {
                    var scale = FontSize / face.Metrics.DesignEmHeight;
                    var ascent = -face.Metrics.Ascent * scale;
                    var descent = face.Metrics.Descent * scale;
                    var gap = face.Metrics.LineGap * scale;

                    context.DrawLine(Draw.Pen(p.Series(2), 2), new Point(x0, baseline - ascent), new Point(x1, baseline - ascent));
                    context.DrawLine(Draw.Pen(p.Series(3), 2), new Point(x0, baseline + descent), new Point(x1, baseline + descent));

                    // The formatter puts half of the line gap above the ascent and half below the descent.
                    if (gap > 0.5)
                    {
                        context.DrawRectangle(p.SeriesFill(6), null, new Rect(x0, baseline - ascent - gap / 2, x1 - x0, gap / 2));
                        context.DrawRectangle(p.SeriesFill(6), null, new Rect(x0, baseline + descent, x1 - x0, gap / 2));
                    }

                    if (labels && (l == 0 || l == _hoverLine))
                    {
                        var lx = x1 + 12;
                        Draw.Text(context, "ascent", lx, baseline - ascent - 16, 16, p.Series(2));
                        Draw.Text(context, "baseline", lx, baseline - 10, 16, p.Caret);
                        Draw.Text(context, "descent", lx, baseline + descent - 4, 16, p.Series(3));
                    }
                }

                context.DrawLine(Draw.Pen(p.Caret, 2), new Point(x0, baseline), new Point(x1, baseline));
                top += line.Height;
            }

            if (labels && face is not null)
            {
                var line = layout.TextLines[Math.Clamp(_hoverLine < 0 ? 0 : _hoverLine, 0, layout.TextLines.Count - 1)];
                var scale = FontSize / face.Metrics.DesignEmHeight;
                Draw.Card(context, p, new Point(Bounds.Width, origin.Y + top + 24), new Rect(origin.X, origin.Y + top + 24,
                        Bounds.Width - origin.X - Pad, Math.Max(10, Bounds.Height - origin.Y - top - 90)),
                    $"Line {Math.Max(0, _hoverLine)} (hover a line)", new[]
                    {
                        ("Height", $"{line.Height:0.##}"),
                        ("Baseline", $"{line.Baseline:0.##}"),
                        ("Extent", $"{line.Extent:0.##}"),
                        ("Primary font", face.FamilyName),
                        ("Ascent, descent, line gap", $"{-face.Metrics.Ascent * scale:0.#}, {face.Metrics.Descent * scale:0.#}, {face.Metrics.LineGap * scale:0.#} at {FontSize} px"),
                    }, 18);
            }
        }

        private void RenderFontSwitch(DrawingContext context)
        {
            var p = Palette;
            var half = (Bounds.Width - 3 * Pad) / 2;
            var left = new Point(Pad, _origin.Y);
            var right = new Point(Pad * 2 + half, _origin.Y);

            Draw.Text(context, State.Typeface.FontFamily.Name, left.X, left.Y - 40, 22, p.Foreground, typeface: Draw.UiBold);
            DrawSample(context, _layouts[0], left);

            if (_alternate is { } alternate)
            {
                Draw.Text(context, _alternateFamily!, right.X, right.Y - 40, 22, p.Foreground, typeface: Draw.UiBold);
                DrawSample(context, alternate, right);
            }
            else
            {
                Draw.Text(context, "no family with different vertical metrics found", right.X, right.Y, 20, p.Muted);
            }

            void DrawSample(DrawingContext ctx, TextLayout layout, Point origin)
            {
                RenderMetrics(ctx, layout, origin, false);
                var line = layout.TextLines[0];
                Draw.Text(ctx, $"line height {line.Height:0.#}, baseline {line.Baseline:0.#}", origin.X,
                    origin.Y + layout.Height + 16, 20, p.Foreground);
            }
        }

        private void RenderTrimming(DrawingContext context)
        {
            var p = Palette;
            var y = _origin.Y - 40;
            var names = new[]
            {
                "CharacterEllipsis", "WordEllipsis", "PrefixCharacterEllipsis", "Wrap, maxLines 1, CharacterEllipsis",
                "Arabic, right to left, CharacterEllipsis",
            };

            for (var i = 0; i < _layouts.Count && i < names.Length; i++)
            {
                var layout = _layouts[i];
                Draw.Text(context, names[i], _origin.X, y, 18, p.Muted);
                y += 24;
                context.DrawRectangle(null, Draw.Pen(p.Grid, 1, dashed: true), new Rect(_origin.X, y, layout.MaxWidth, layout.Height));
                layout.Draw(context, new Point(_origin.X, y));
                y += layout.Height + 12;
            }
        }

        private void EnsureLayout()
        {
            if (_layout is not null)
            {
                return;
            }

            var analysis = State.Analysis;
            _origin = new Point(Pad, Pad + 100);
            _layout = analysis.CreateLayout(FontSize, WrapWidth(), TextWrapping.Wrap);

            if (Step == 5)
            {
                var sample = SampleText();
                var half = (Bounds.Width - 3 * Pad) / 2;
                _layouts.Add(analysis.CreateLayout(FontSize, half, TextWrapping.Wrap, text: sample));
                _alternateFamily = PickAlternateFamily();

                if (_alternateFamily is not null)
                {
                    _alternate = analysis.CreateLayout(FontSize, half, TextWrapping.Wrap,
                        typeface: new Typeface(_alternateFamily), text: sample);
                }
            }
            else if (Step == 6)
            {
                // Trimming needs text wider than the box, so the box never exceeds 70 % of the string.
                var size = 36;
                var width = Math.Min(WrapWidth(), analysis.Layout.WidthIncludingTrailingWhitespace * size / analysis.FontSize * 0.7);
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.WordEllipsis));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.PrefixCharacterEllipsis));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.Wrap, TextTrimming.CharacterEllipsis, maxLines: 1));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
                    flowDirection: FlowDirection.RightToLeft, text: Preset.All.First(x => x.Name == "Arabic").Text));
            }
        }

        // The Latin start of the string up to the first right-to-left or non-Latin character, so the
        // second font, which may not cover the rest, renders the same characters as the first.
        private string SampleText()
        {
            var text = State.Text;
            var end = 0;

            foreach (var cp in State.Analysis.Codepoints)
            {
                if (cp.Codepoint.Value > 0x24F)
                {
                    break;
                }

                end = cp.Offset + cp.Length;
            }

            return end > 2 ? text.Substring(0, end).TrimEnd() : "Office ffi";
        }

        private static string? PickAlternateFamily()
        {
            // Script and display faces with tall ascenders or large line gaps on Windows and macOS.
            foreach (var family in new[] { "Gabriola", "Cambria Math", "Zapfino", "Snell Roundhand", "Apple Chancery", "Georgia" })
            {
                if (FontManager.Current.SystemFonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal,
                        FontStretch.Normal, out var face) &&
                    string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                {
                    return family;
                }
            }

            return null;
        }

        private int LineAt(double y)
        {
            var top = 0.0;

            for (var i = 0; i < _layout!.TextLines.Count; i++)
            {
                top += _layout.TextLines[i].Height;

                if (y < top)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int StartOf(TextRun run) =>
            MemoryMarshal.TryGetString(run.Text, out _, out var start, out _) ? start : 0;

        private double ContentWidth() => Math.Max(200, Bounds.Width - 2 * Pad - 280);

        private double WrapWidth() => Math.Max(80, ContentWidth() * State.WrapFraction);

        private void UpdateMixTransition()
        {
            Transitions = State.Animate ?
                new Transitions
                {
                    new DoubleTransition { Property = StepProgressProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = new CubicEaseOut() },
                    new DoubleTransition { Property = VisualMixProperty, Duration = TimeSpan.FromMilliseconds(900), Easing = new CubicEaseInOut() },
                } :
                null;
        }

        private void DisposeLayouts()
        {
            _layout?.Dispose();
            _layout = null;
            _alternate?.Dispose();
            _alternate = null;

            foreach (var layout in _layouts)
            {
                layout.Dispose();
            }

            _layouts.Clear();
        }

        public override void Dispose() => DisposeLayouts();
    }
}
