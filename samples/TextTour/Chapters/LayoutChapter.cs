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

        // The reading-order band under each line: the stop row, jump lanes above and below it, and
        // the numbered labels on the stops.
        private const double LaneGap = 12;
        private const double LaneClearance = 24;
        private const double BandEdge = 12;
        private const double BandSpacing = 12;
        private const double LabelSize = 14;
        private const double LabelHeight = 20;
        private const double DotRadius = 4;
        private const double StretchHead = 12;
        private const double JumpHead = 10;

        // How far a jump leaving a stop starts beside the jump landing on it.
        private const double PortOffset = 5;

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
        private IReadOnlyList<IReadOnlyList<PathStop>>? _pathStops;

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
        /// Lays out the reading-order band under each line: the stops of the line's graphemes in
        /// logical order, the stretches between screen neighbours and the jumps between the rest.
        /// <paramref name="mixes"/> are the run order mixes the band must have room for; the first
        /// is the one drawn now.
        /// </summary>
        private PathPlan PlanPath(params double[] mixes)
        {
            var stops = PathStops();
            var plan = new PathPlan();

            foreach (var mix in mixes)
            {
                foreach (var line in stops)
                {
                    var xs = line.Select(s => s.X + s.RunShift * (1 - mix)).ToArray();
                    plan.Dense |= IsDense(line, xs);
                }
            }

            foreach (var line in stops)
            {
                var upper = 0;
                var lower = 0;
                PathRoute? current = null;

                foreach (var mix in mixes)
                {
                    var xs = line.Select(s => s.X + s.RunShift * (1 - mix)).ToArray();
                    var route = Route(line, xs, plan.Dense);
                    current ??= route;
                    upper = Math.Max(upper, route.Upper);
                    lower = Math.Max(lower, route.Lower);
                }

                current!.StopRow = StopRowOffset(upper);
                plan.Routes.Add(current);
                plan.Bands.Add(current.StopRow + BandBottom(lower) + BandSpacing);
            }

            return plan;

            static double StopRowOffset(int upper) =>
                upper > 0 ? BandEdge + (upper - 1) * LaneGap + LaneClearance : LabelHeight / 2 + 6;

            static double BandBottom(int lower) =>
                lower > 0 ? LaneClearance + (lower - 1) * LaneGap + BandEdge : LabelHeight / 2 + 4;
        }

        /// <summary>
        /// The graphemes of each line with their centre from <see cref="TextLine.GetTextBounds"/>, the
        /// bidi level of their run and the offset that moves their run from its visual to its logical
        /// slot. Numbers run on across lines.
        /// </summary>
        private IReadOnlyList<IReadOnlyList<PathStop>> PathStops()
        {
            if (_pathStops is not null)
            {
                return _pathStops;
            }

            var analysis = State.Analysis;
            var lines = new List<IReadOnlyList<PathStop>>();
            var number = 1;

            foreach (var line in _layout!.TextLines)
            {
                var start = line.FirstTextSourceIndex;
                var end = start + line.Length - line.NewLineLength;
                var shifts = RunShifts(line);
                var stops = new List<PathStop>();

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

                    var (level, shift) = (0, 0.0);

                    foreach (var run in shifts)
                    {
                        if (g.Start >= run.Start && g.Start < run.End)
                        {
                            (level, shift) = (run.Level, run.Shift);
                            break;
                        }
                    }

                    stops.Add(new PathStop(number++, _origin.X + rect.Center.X, rect.Width / 2, level, shift));
                }

                lines.Add(stops);
            }

            return _pathStops = lines;
        }

        /// <summary>
        /// The runs of a line with their bidi level and the distance from their visual to their
        /// logical slot, where the line lays the runs out in the order the text source handed them out.
        /// </summary>
        private static List<(int Start, int End, int Level, double Shift)> RunShifts(TextLine line)
        {
            var runs = line.TextRuns.OfType<DrawableTextRun>().ToList();
            var logicalX = new Dictionary<TextRun, double>();
            var x = line.Start;

            foreach (var run in runs.OrderBy(TextAnalysis.StartOf))
            {
                logicalX[run] = x;
                x += run.Size.Width;
            }

            var result = new List<(int, int, int, double)>();
            x = line.Start;

            foreach (var run in runs)
            {
                var start = TextAnalysis.StartOf(run);
                result.Add((start, start + run.Length, run is ShapedTextRun s ? s.BidiLevel : 0, logicalX[run] - x));
                x += run.Size.Width;
            }

            return result;
        }

        // Stops are dense when two screen neighbours lack room for both labels and a stretch arrowhead
        // between them; then only the first stop of each stretch, which includes every landing, is numbered.
        private static bool IsDense(IReadOnlyList<PathStop> stops, IReadOnlyList<double> xs)
        {
            var order = Enumerable.Range(0, stops.Count).OrderBy(i => xs[i]).ToList();

            for (var k = 1; k < order.Count; k++)
            {
                var a = order[k - 1];
                var b = order[k];

                if (xs[b] - xs[a] < (LabelWidth(stops[a].Number) + LabelWidth(stops[b].Number)) / 2 + StretchHead + 6)
                {
                    return true;
                }
            }

            return false;
        }

        private static double LabelWidth(int number) =>
            Math.Max(LabelHeight, number.ToString().Length * LabelSize * 0.62 + 10);

        /// <summary>
        /// Splits a line's stops into stretches of screen neighbours that read the same way and the
        /// jumps between them, and gives each jump a side and a lane. Two jumps whose ends interleave
        /// would cross on the same side, so they go to opposite sides of the stop row; nested jumps on
        /// one side take lanes further out the more they enclose, so connectors never touch.
        /// </summary>
        private static PathRoute Route(IReadOnlyList<PathStop> stops, IReadOnlyList<double> xs, bool dense)
        {
            var route = new PathRoute { Xs = xs };
            var first = 0;
            var sign = 0;

            for (var k = 1; k <= stops.Count; k++)
            {
                if (k < stops.Count && Neighbours(k - 1, k))
                {
                    var s = xs[k] < xs[k - 1] ? -1 : 1;

                    if (sign == 0 || s == sign)
                    {
                        sign = s;
                        continue;
                    }

                    // The direction turns between two neighbours: the turning stop ends one stretch
                    // and starts the next.
                    route.Stretches.Add((first, k - 1, sign));
                    first = k - 1;
                    sign = s;
                    continue;
                }

                if (stops.Count > 0)
                {
                    route.Stretches.Add((first, k - 1, sign));
                }

                if (k < stops.Count)
                {
                    var jump = new PathJump(k - 1, k, xs[k - 1], xs[k]);

                    // A stop that is landed on and left again by jumps keeps the two connectors apart,
                    // on the side that does not interleave them.
                    if (route.Jumps.Count > 0 && route.Jumps[^1].To == k - 1)
                    {
                        var previous = route.Jumps[^1];
                        jump.FromX = xs[k - 1] + PortOffset;

                        if (Interleaved(previous, jump))
                        {
                            jump.FromX = xs[k - 1] - PortOffset;
                        }
                    }

                    route.Jumps.Add(jump);
                }

                first = k;
                sign = 0;
            }

            foreach (var (stretchFirst, _, _) in route.Stretches)
            {
                route.Labelled.Add(stretchFirst);
            }

            if (!dense)
            {
                for (var k = 0; k < stops.Count; k++)
                {
                    route.Labelled.Add(k);
                }
            }

            AssignSides(route.Jumps);

            foreach (var above in new[] { false, true })
            {
                var side = route.Jumps.Where(j => j.Above == above).OrderBy(j => j.Hi - j.Lo).ToList();
                var lanes = 0;

                for (var n = 0; n < side.Count; n++)
                {
                    var jump = side[n];
                    var lane = 0;

                    for (var m = 0; m < n; m++)
                    {
                        if (side[m].Lo >= jump.Lo - 0.5 && side[m].Hi <= jump.Hi + 0.5)
                        {
                            lane = Math.Max(lane, side[m].Lane + 1);
                        }
                    }

                    while (side.Take(n).Any(o => o.Lane == lane && o.Lo < jump.Hi + 6 && jump.Lo < o.Hi + 6))
                    {
                        lane++;
                    }

                    jump.Lane = lane;
                    lanes = Math.Max(lanes, lane + 1);
                }

                if (above)
                {
                    route.Upper = lanes;
                }
                else
                {
                    route.Lower = lanes;
                }
            }

            return route;

            bool Neighbours(int a, int b) => Math.Abs(xs[b] - xs[a]) <= stops[a].HalfWidth + stops[b].HalfWidth + 2;
        }

        // Jumps go below the stop row unless they interleave with another jump below; then the
        // interleaving ones are two-coloured, with the larger group below.
        private static void AssignSides(List<PathJump> jumps)
        {
            var side = new int[jumps.Count];
            Array.Fill(side, -1);

            for (var s = 0; s < jumps.Count; s++)
            {
                if (side[s] >= 0)
                {
                    continue;
                }

                var component = new List<int> { s };
                var bipartite = true;
                side[s] = 0;

                for (var q = 0; q < component.Count; q++)
                {
                    var u = component[q];

                    for (var t = 0; t < jumps.Count; t++)
                    {
                        if (t == u || !Interleaved(jumps[u], jumps[t]))
                        {
                            continue;
                        }

                        if (side[t] < 0)
                        {
                            side[t] = 1 - side[u];
                            component.Add(t);
                        }
                        else if (side[t] == side[u])
                        {
                            bipartite = false;
                        }
                    }
                }

                if (!bipartite)
                {
                    // An odd cycle of interleaved jumps cannot avoid every crossing; the longest
                    // jumps take the free side first.
                    foreach (var c in component)
                    {
                        side[c] = -1;
                    }

                    foreach (var c in component.OrderByDescending(c => jumps[c].Hi - jumps[c].Lo))
                    {
                        side[c] = component.Any(o => side[o] == 0 && Interleaved(jumps[o], jumps[c])) &&
                                  !component.Any(o => side[o] == 1 && Interleaved(jumps[o], jumps[c])) ? 1 : 0;
                    }
                }
                else if (component.Count(c => side[c] == 1) > component.Count(c => side[c] == 0))
                {
                    foreach (var c in component)
                    {
                        side[c] = 1 - side[c];
                    }
                }
            }

            for (var i = 0; i < jumps.Count; i++)
            {
                jumps[i].Above = side[i] == 1;
            }
        }

        private static bool Interleaved(PathJump a, PathJump b) =>
            (a.Lo < b.Lo && b.Lo < a.Hi && a.Hi < b.Hi) || (b.Lo < a.Lo && a.Lo < b.Hi && b.Hi < a.Hi);

        /// <summary>
        /// Draws the reading-order band of each line under the line: numbered stops at the grapheme
        /// centres, a solid stretch in the run's level colour with one arrowhead for each run of screen
        /// neighbours that read the same way, and dashed jumps in lanes above or below the stop row with
        /// the head where they land. <paramref name="bandTops"/> gives the top of each line's band.
        /// </summary>
        private void RenderReadingPath(DrawingContext context, PathPlan plan, IReadOnlyList<double> bandTops)
        {
            var p = Palette;
            var stops = PathStops();
            var jumpPen = new Pen(p.Muted, 2, new DashStyle(new double[] { 3, 2 }, 0), PenLineCap.Flat);
            var halo = new Pen(p.Background, 6);

            for (var l = 0; l < stops.Count; l++)
            {
                var line = stops[l];
                var route = plan.Routes[l];
                var xs = route.Xs;
                var y = bandTops[l] + route.StopRow;

                // Stretches, then their heads, so a head is never cut by the next segment.
                foreach (var (first, last, _) in route.Stretches)
                {
                    for (var k = first + 1; k <= last; k++)
                    {
                        context.DrawLine(new Pen(LevelBrush(line[k].Level), 3), new Point(xs[k - 1], y), new Point(xs[k], y));
                    }
                }

                foreach (var (first, last, sign) in route.Stretches)
                {
                    if (last == first)
                    {
                        continue;
                    }

                    var gap = HeadGap(first, last);
                    var mid = (xs[gap] + xs[gap + 1]) / 2;
                    Draw.Head(context, LevelBrush(line[gap + 1].Level), new Point(mid + sign * StretchHead / 2, y),
                        new Vector(sign, 0), StretchHead);
                }

                foreach (var jump in route.Jumps)
                {
                    var geometry = JumpGeometry(jump, y, out var tip);
                    context.DrawGeometry(null, halo, geometry);
                    context.DrawGeometry(null, jumpPen, geometry);
                    Draw.Head(context, p.Muted, tip, new Vector(0, jump.Above ? 1 : -1), JumpHead);
                }

                for (var k = 0; k < line.Count; k++)
                {
                    var brush = LevelBrush(line[k].Level);

                    if (!route.Labelled.Contains(k))
                    {
                        context.DrawEllipse(brush, null, new Point(xs[k], y), DotRadius, DotRadius);
                        continue;
                    }

                    var width = LabelWidth(line[k].Number);
                    var box = new Rect(xs[k] - width / 2, y - LabelHeight / 2, width, LabelHeight);
                    context.DrawRectangle(brush, Draw.Pen(p.Background, 1.5), box, LabelHeight / 2, LabelHeight / 2);
                    Draw.TextIn(context, line[k].Number.ToString(), box, LabelSize, p.Background, Draw.UiBold, padding: 2);
                }

                // The gap nearest the middle of a stretch whose ends are plain dots, so the head sits
                // clear of the labels; any gap when every one touches a label.
                int HeadGap(int first, int last)
                {
                    var middle = (first + last) / 2.0 - 0.5;

                    return Enumerable.Range(first, last - first)
                        .OrderBy(g => route.Labelled.Contains(g) || route.Labelled.Contains(g + 1) ? 1 : 0)
                        .ThenBy(g => Math.Abs(g - middle))
                        .First();
                }

                double Attach(int k) => route.Labelled.Contains(k) ? LabelHeight / 2 : DotRadius;

                Geometry JumpGeometry(PathJump jump, double stopY, out Point tip)
                {
                    var dir = jump.Above ? -1 : 1;
                    var laneY = stopY + dir * (LaneClearance + jump.Lane * LaneGap);
                    var fromY = stopY + dir * Attach(jump.From);
                    tip = new Point(jump.ToX, stopY + dir * Attach(jump.To));
                    var endY = tip.Y + dir * JumpHead * 0.7;
                    var corner = Math.Min(5, Math.Abs(jump.ToX - jump.FromX) / 2);
                    var h = jump.ToX > jump.FromX ? 1 : -1;
                    var geometry = new StreamGeometry();

                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(new Point(jump.FromX, fromY), false);
                        g.LineTo(new Point(jump.FromX, laneY - dir * corner));
                        g.QuadraticBezierTo(new Point(jump.FromX, laneY), new Point(jump.FromX + h * corner, laneY));
                        g.LineTo(new Point(jump.ToX - h * corner, laneY));
                        g.QuadraticBezierTo(new Point(jump.ToX, laneY), new Point(jump.ToX, laneY - dir * corner));
                        g.LineTo(new Point(jump.ToX, endY));
                        g.EndFigure(false);
                    }

                    return geometry;
                }
            }

            IBrush LevelBrush(int level) => level == 0 ? p.Foreground : p.Series(level + 2);
        }

        /// <summary>
        /// The top of each line's text, with <paramref name="above"/> reserved over every line and
        /// <paramref name="below"/> under it.
        /// </summary>
        private IReadOnlyList<double> LineTops(double above, IReadOnlyList<double>? below)
        {
            var tops = new List<double>();
            var top = 0.0;

            for (var l = 0; l < _layout!.TextLines.Count; l++)
            {
                tops.Add(top + above);
                top += _layout.TextLines[l].Height + above + (below?[l] ?? 0);
            }

            return tops;
        }

        private sealed class PathPlan
        {
            public bool Dense { get; set; }

            public List<PathRoute> Routes { get; } = new();

            /// <summary>The height reserved under each line.</summary>
            public List<double> Bands { get; } = new();
        }

        private sealed class PathRoute
        {
            public IReadOnlyList<double> Xs { get; init; } = Array.Empty<double>();

            public List<(int First, int Last, int Sign)> Stretches { get; } = new();

            public List<PathJump> Jumps { get; } = new();

            public HashSet<int> Labelled { get; } = new();

            public int Upper { get; set; }

            public int Lower { get; set; }

            /// <summary>The distance from the band's top to its stop row.</summary>
            public double StopRow { get; set; }
        }

        private sealed class PathJump
        {
            public PathJump(int from, int to, double fromX, double toX)
            {
                From = from;
                To = to;
                FromX = fromX;
                ToX = toX;
            }

            public int From { get; }

            public int To { get; }

            public double FromX { get; set; }

            public double ToX { get; }

            public bool Above { get; set; }

            public int Lane { get; set; }

            public double Lo => Math.Min(FromX, ToX);

            public double Hi => Math.Max(FromX, ToX);
        }

        private readonly record struct PathStop(int Number, double X, double HalfWidth, int Level, double RunShift);

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
            var plan = PathVisible ? PlanPath(1) : null;
            var tops = LineTops(0, plan?.Bands);
            var height = tops[^1] + layout.TextLines[^1].Height + (plan?.Bands[^1] ?? 0);
            RenderWidthGuide(context, height);

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                layout.TextLines[l].Draw(context, new Point(_origin.X, _origin.Y + tops[l]));
            }

            if (plan is not null)
            {
                RenderReadingPath(context, plan, BandTops(tops));
            }

            if (!breaks)
            {
                return;
            }

            var analysis = State.Analysis;

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var top = tops[l];
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
            }
        }

        // The top of the band under each line, in view coordinates.
        private IReadOnlyList<double> BandTops(IReadOnlyList<double> tops) =>
            tops.Select((top, l) => _origin.Y + top + _layout!.TextLines[l].Height).ToList();

        private void RenderOrder(DrawingContext context)
        {
            var p = Palette;
            var layout = _layout!;
            var mix = VisualMix;

            // The band keeps room for the jumps of both orders, so the lines below stay put while
            // the runs move.
            var plan = PathVisible ? PlanPath(mix, 0, 1) : null;
            var tops = LineTops(ArrowStrip, plan?.Bands);
            var height = tops[^1] + layout.TextLines[^1].Height + (plan?.Bands[^1] ?? 0);

            // The direction arrows are drawn last, so nothing covers them.
            var arrows = new List<(Rect Box, bool RightToLeft)>();

            RenderWidthGuide(context, height);

            for (var l = 0; l < layout.TextLines.Count; l++)
            {
                var line = layout.TextLines[l];
                var top = tops[l];
                var runs = line.TextRuns.OfType<DrawableTextRun>().ToList();
                var shifts = RunShifts(line);
                var x = line.Start;

                for (var r = 0; r < runs.Count; r++)
                {
                    var run = runs[r];
                    var (start, _, level, fullShift) = shifts[r];
                    var rx = _origin.X + x + fullShift * (1 - mix);
                    x += run.Size.Width;

                    // The box holds a strip above the text for the arrow of the run's reading direction.
                    var box = new Rect(rx, _origin.Y + top - ArrowStrip, run.Size.Width, line.Height + ArrowStrip);
                    context.DrawRectangle(level % 2 == 1 ? p.SeriesFill(3) : level > 0 ? p.SeriesFill(4) : null,
                        Draw.Pen(level == 0 ? p.Grid : p.Series(level + 2), 1.5), box.Deflate(new Thickness(1, 2)), 4, 4);
                    arrows.Add((new Rect(box.X + 1, box.Y + 3, box.Width - 2, ArrowStrip - 2), level % 2 == 1));
                    run.Draw(context, new Point(rx, _origin.Y + top + TextLineImpl.GetBaselineOffset(line, run)));
                    Draw.Text(context, $"{start}", rx + 4, box.Bottom - 20, 14, p.Muted);
                }
            }

            if (plan is not null)
            {
                RenderReadingPath(context, plan, BandTops(tops));
            }

            foreach (var (box, rightToLeft) in arrows)
            {
                Draw.DirectionArrow(context, box, rightToLeft, p.Foreground, 44, 2.5);
            }

            Draw.Text(context, (mix < 0.5 ?
                    "runs in logical order (as the text source handed them out); press V" :
                    "runs in visual order (as the line draws them); press V") + ", P for the reading order",
                _origin.X, _origin.Y + height + 24, 20, p.Muted);
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
            _pathStops = null;
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
