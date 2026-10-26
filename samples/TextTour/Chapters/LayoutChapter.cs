using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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
        private const double FontSize = 64;

        // Height of the strip above each line in the run order step that holds the direction arrows.
        private const double ArrowStrip = 26;

        private static readonly (bool Path, double Mix, string Name)[] s_orderVariants =
        {
            (false, 0, "-logical"), (false, 1, "-visual"), (true, 0, "-logical-path"), (true, 1, "-visual-path"),
            (true, 0.5, "-midway-path"),
        };

        // The paragraph direction of the wrapped string and of the samples beside it.
        private readonly FlowDirection _flowDirection = FlowDirection.LeftToRight;

        // The paragraph direction of the right-to-left trimming sample.
        private readonly FlowDirection _rtlFlowDirection = FlowDirection.RightToLeft;

        private readonly Tween _visualMix;
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
        private bool _showPath;

        public LayoutChapter(TourState state) : base(state)
        {
            _visualMix = AddTween(0, TimeSpan.FromMilliseconds(900), Tween.EaseInOut);
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

        /// <summary>0 draws each line's runs in logical order, 1 in visual order.</summary>
        private double VisualMix => _visualMix.Value;

        /// <summary>The steps that draw the wrapped string at its real positions, where P draws the reading order.</summary>
        private bool PathStep => Step <= 3;

        private bool PathVisible => _showPath && PathStep;

        public override string StepName => Step switch
        {
            1 => "lines at a width",
            2 => "break opportunities",
            3 => _visualMix.Target < 0.5 ? "logical order (V)" : "visual order (V)",
            4 => "line metrics",
            5 => "another font",
            _ => "trimming",
        };

        public override string Caption => (Step switch
        {
            1 => $"new TextLayout(..., FlowDirection.{_flowDirection}, TextWrapping.Wrap, maxWidth: {WrapWidth():0})  ->  " +
                 "TextLayout.TextLines",
            2 => "LineBreakEnumerator  ->  TextLine.Length, TextLine.TextLineBreak",
            3 => "TextLine.TextRuns (in visual order once the line is finalized)  ->  ShapedTextRun.BidiLevel",
            4 => "TextLine.Baseline, .Height, .Extent; GlyphTypeface.Metrics.Ascent, .Descent, .LineGap",
            5 => "GlyphTypeface.Metrics: each font brings its own ascent, descent and line gap",
            _ => "TextTrimming.CharacterEllipsis, .WordEllipsis, .PrefixCharacterEllipsis; maxLines; " +
                 "FlowDirection.RightToLeft",
        }) + (PathVisible ? "   path: TextLine.GetTextBounds per grapheme" : string.Empty);

        public override int ExportVariantCount(int step) => step switch
        {
            1 => 2,
            3 => s_orderVariants.Length,
            _ => 1,
        };

        public override string ExportVariantName(int step, int variant) => step switch
        {
            1 => variant == 1 ? "-path" : string.Empty,
            3 => s_orderVariants[variant].Name,
            _ => string.Empty,
        };

        public override void PrepareExport(int step, int variant)
        {
            var order = step == 3 ? s_orderVariants[variant] : default;
            _visualMix.To(order.Mix, false);
            _showPath = step == 1 ? variant == 1 : order.Path;
            _hoverLine = step == 4 ? 0 : -1;
            RaiseCaptionChanged();
            InvalidateVisual();
        }

        public override void EndExport()
        {
            _showPath = false;
            _visualMix.To(0, false);
        }

        public override bool HandleKey(Key key, KeyModifiers modifiers)
        {
            if (Step == 3 && key == Key.V)
            {
                AnimateTo(_visualMix, _visualMix.Target < 0.5 ? 1 : 0);
                RaiseCaptionChanged();
                return true;
            }

            if (PathStep && key == Key.P)
            {
                _showPath = !_showPath;
                RaiseCaptionChanged();
                InvalidateVisual();
                return true;
            }

            return false;
        }

        protected override void OnStepChanged()
        {
            if (Step != 3)
            {
                _visualMix.To(0, false);
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

            RenderParagraphBadge(context);
        }

        // The paragraph direction of the wrapped string, beside the width slider.
        private void RenderParagraphBadge(DrawingContext context)
        {
            if (Child is not { } bar)
            {
                return;
            }

            var detail = $"level {TextAnalysis.ResolveParagraphLevel(State.Text, _flowDirection)}";
            var size = 17.0;
            var height = size * 2.3;
            Draw.Badge(context, Palette, bar.Bounds.Right + 32, bar.Bounds.Y + (bar.Bounds.Height - height) / 2,
                $"paragraph: {Draw.Direction(_flowDirection)}", size, detail);
        }

        /// <summary>
        /// Draws a line through the graphemes of each line in logical order, at their real positions,
        /// with an arrowhead per segment. Neighbours on screen are joined straight; a jump to a
        /// character elsewhere on the line arcs off it. <paramref name="lineTops"/> gives the
        /// top of each line's text; <paramref name="shift"/> moves a character with its run while the
        /// runs animate.
        /// </summary>
        private void RenderReadingPath(DrawingContext context, IReadOnlyList<double> lineTops, Func<TextLine, int, double>? shift)
        {
            var p = Palette;
            var analysis = State.Analysis;
            var layout = _layout!;
            var brush = p.Caret;
            var pen = new Pen(brush, 2.5);
            var dashed = new Pen(brush, 2.5, new DashStyle(new double[] { 3, 2 }, 0));
            var halo = new Pen(p.Background, 6.5);
            var head = 14.0;

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var start = line.FirstTextSourceIndex;
                var end = start + line.Length - line.NewLineLength;
                var y = _origin.Y + lineTops[l] + line.Baseline - FontSize * 0.3;
                var points = new List<(double X, double HalfWidth)>();

                foreach (var g in analysis.Graphemes)
                {
                    if (g.Start < start || g.End > end)
                    {
                        continue;
                    }

                    var rect = default(Rect);
                    var first = true;

                    foreach (var b in line.GetTextBounds(g.Start, g.Length))
                    {
                        rect = first ? b.Rectangle : rect.Union(b.Rectangle);
                        first = false;
                    }

                    if (first || rect.Width < 0.5)
                    {
                        continue;
                    }

                    points.Add((_origin.X + rect.Center.X + (shift?.Invoke(line, g.Start) ?? 0), rect.Width / 2));
                }

                // Consecutive neighbours that read the same way form one stretch with one arrowhead
                // in its middle. A jump is a dashed arc with its head where it lands; it bends above
                // the line when it goes right and below when it goes left, and a longer jump bends
                // further, so jumps over the same characters stay apart.
                var strokes = new List<(Geometry Geometry, bool Jump)>();
                var heads = new List<(Point Tip, Vector Direction)>();
                var i = 1;

                while (i < points.Count)
                {
                    var a = points[i - 1];
                    var b = points[i];
                    var sign = b.X < a.X ? -1 : 1;

                    if (!Neighbours(a, b))
                    {
                        var lift = sign * Math.Min(line.Height * 1.2, 10 + Math.Abs(b.X - a.X) * 0.45);
                        var from = new Point(a.X, y);
                        var to = new Point(b.X, y);
                        var c1 = new Point(a.X + (b.X - a.X) * 0.15, y - lift);
                        var c2 = new Point(b.X - (b.X - a.X) * 0.15, y - lift);
                        var geometry = new StreamGeometry();

                        using (var g = geometry.Open())
                        {
                            g.BeginFigure(from, false);
                            g.CubicBezierTo(c1, c2, to);
                            g.EndFigure(false);
                        }

                        strokes.Add((geometry, true));

                        // The head sits on the curve just before the landing dot, along the tangent there.
                        var t = 0.9;
                        var u = 1 - t;
                        var onCurve = new Point(
                            u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                            u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y);
                        var tangent = 3 * u * u * (c1 - from) + 6 * u * t * (c2 - c1) + 3 * t * t * (to - c2);
                        heads.Add((onCurve, tangent));
                        i++;
                        continue;
                    }

                    var stretchFirst = i - 1;

                    while (i < points.Count && Neighbours(points[i - 1], points[i]) &&
                           (points[i].X < points[i - 1].X ? -1 : 1) == sign)
                    {
                        i++;
                    }

                    var stretchStart = new Point(points[stretchFirst].X, y);
                    var stretchEnd = new Point(points[i - 1].X, y);
                    strokes.Add((new LineGeometry(stretchStart, stretchEnd), false));
                    heads.Add((new Point((stretchStart.X + stretchEnd.X) / 2 + sign * head / 2, y), new Vector(sign, 0)));
                }

                foreach (var (geometry, _) in strokes)
                {
                    context.DrawGeometry(null, halo, geometry);
                }

                foreach (var (geometry, jump) in strokes)
                {
                    context.DrawGeometry(null, jump ? dashed : pen, geometry);
                }

                foreach (var (x, _) in points)
                {
                    context.DrawEllipse(brush, Draw.Pen(p.Background, 1.5), new Point(x, y), 3.5, 3.5);
                }

                foreach (var (tip, direction) in heads)
                {
                    Draw.Head(context, brush, tip, direction, head);
                }
            }

            static bool Neighbours((double X, double HalfWidth) a, (double X, double HalfWidth) b) =>
                Math.Abs(b.X - a.X) <= a.HalfWidth + b.HalfWidth + 2;
        }

        private IReadOnlyList<double> LineTops(double extra)
        {
            var tops = new List<double>();
            var top = 0.0;

            foreach (var line in _layout!.TextLines)
            {
                tops.Add(top + extra);
                top += line.Height + extra;
            }

            return tops;
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

            if (PathVisible)
            {
                RenderReadingPath(context, LineTops(0), null);
            }

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
            var tops = LineTops(ArrowStrip);
            var shifts = new Dictionary<TextLine, List<(int Start, int End, double Shift)>>();

            // The direction arrows are drawn last, so the reading path never covers them.
            var arrows = new List<(Rect Box, bool RightToLeft)>();

            RenderWidthGuide(context, layout.Height + ArrowStrip * layout.TextLines.Count);

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var top = tops[l];
                var runs = line.TextRuns.OfType<DrawableTextRun>().ToList();
                var logical = runs.OrderBy(TextAnalysis.StartOf).ToList();
                var logicalX = new Dictionary<TextRun, double>();
                var visualX = new Dictionary<TextRun, double>();
                var lineShifts = shifts[line] = new List<(int, int, double)>();

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
                    var shift = (logicalX[run] - visualX[run]) * (1 - mix);
                    var rx = _origin.X + visualX[run] + shift;
                    var level = run is ShapedTextRun s ? s.BidiLevel : 0;
                    var start = TextAnalysis.StartOf(run);
                    lineShifts.Add((start, start + run.Length, shift));

                    // The box holds a strip above the text for the arrow of the run's reading direction.
                    var box = new Rect(rx, _origin.Y + top - ArrowStrip, run.Size.Width, line.Height + ArrowStrip);
                    context.DrawRectangle(level % 2 == 1 ? p.SeriesFill(3) : level > 0 ? p.SeriesFill(4) : null,
                        Draw.Pen(level == 0 ? p.Grid : p.Series(level + 2), 1.5), box.Deflate(new Thickness(1, 2)), 4, 4);
                    arrows.Add((new Rect(box.X + 1, box.Y + 3, box.Width - 2, ArrowStrip - 2), level % 2 == 1));
                    run.Draw(context, new Point(rx, _origin.Y + top + TextLineImpl.GetBaselineOffset(line, run)));
                    Draw.Text(context, $"{start}", rx + 4, box.Bottom - 20, 14, p.Muted);
                }
            }

            if (PathVisible)
            {
                RenderReadingPath(context, tops, (line, index) =>
                {
                    foreach (var (s, e, shift) in shifts[line])
                    {
                        if (index >= s && index < e)
                        {
                            return shift;
                        }
                    }

                    return 0;
                });
            }

            foreach (var (box, rightToLeft) in arrows)
            {
                Draw.DirectionArrow(context, box, rightToLeft, p.Foreground, 44, 2.5);
            }

            Draw.Text(context, (mix < 0.5 ?
                    "runs in logical order (as the text source handed them out); press V" :
                    "runs in visual order (as the line draws them); press V") + ", P for the reading order",
                _origin.X, _origin.Y + tops[^1] + layout.TextLines[^1].Height + 24, 20, p.Muted);
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

                // The right-to-left sample is a paragraph of its own direction; its badge sits
                // beside the sample box.
                if (i == 4)
                {
                    var size = 16.0;
                    Draw.Badge(context, p, _origin.X + layout.MaxWidth + 16, y + (layout.Height - size * 2.3) / 2,
                        $"paragraph: {Draw.Direction(_rtlFlowDirection)}", size,
                        $"level {TextAnalysis.ResolveParagraphLevel(RtlSample, _rtlFlowDirection)}");
                }

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
            _layout = analysis.CreateLayout(FontSize, WrapWidth(), TextWrapping.Wrap, flowDirection: _flowDirection);

            if (Step == 5)
            {
                var sample = SampleText();
                var half = (Bounds.Width - 3 * Pad) / 2;
                _layouts.Add(analysis.CreateLayout(FontSize, half, TextWrapping.Wrap, flowDirection: _flowDirection,
                    text: sample));
                _alternateFamily = PickAlternateFamily();

                if (_alternateFamily is not null)
                {
                    _alternate = analysis.CreateLayout(FontSize, half, TextWrapping.Wrap, flowDirection: _flowDirection,
                        typeface: new Typeface(_alternateFamily), text: sample);
                }
            }
            else if (Step == 6)
            {
                // Trimming needs text wider than the box, so the box never exceeds 70 % of the string.
                var size = 36;
                var width = Math.Min(WrapWidth(), analysis.Layout.WidthIncludingTrailingWhitespace * size / analysis.FontSize * 0.7);
                var flow = _flowDirection;
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis, flowDirection: flow));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.WordEllipsis, flowDirection: flow));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.PrefixCharacterEllipsis,
                    flowDirection: flow));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.Wrap, TextTrimming.CharacterEllipsis, maxLines: 1,
                    flowDirection: flow));
                _layouts.Add(analysis.CreateLayout(size, width, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
                    flowDirection: _rtlFlowDirection, text: RtlSample));
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

        // Script and display faces with tall ascenders or large line gaps on Windows and macOS.
        private static string? PickAlternateFamily() =>
            SystemFamilies.FirstOf("Gabriola", "Cambria Math", "Zapfino", "Snell Roundhand", "Apple Chancery", "Georgia");

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

        private static string RtlSample => Preset.All.First(x => x.Name == "Arabic").Text;

        private double ContentWidth() => Math.Max(200, Bounds.Width - 2 * Pad - 280);

        private double WrapWidth() => Math.Max(80, ContentWidth() * State.WrapFraction);

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
