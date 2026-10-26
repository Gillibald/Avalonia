using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour.Chapters
{
    /// <summary>
    /// Mapping between points and text positions on the wrapped string: a click becomes a
    /// <see cref="CharacterHit"/>, the caret walks the caret stops, the ligature gets stops inside one
    /// glyph, a BiDi boundary gets two caret positions for one index, and a drag selects with the
    /// rectangles <see cref="TextLayout.HitTestTextRange"/> returns.
    /// </summary>
    internal sealed class HitsChapter : Chapter
    {
        private TextLayout? _layout;
        private TextLayout? _ligatureLayout;
        private string? _ligatureFamily;
        private List<CaretStop>? _stops;
        private Point _origin;
        private Point? _clickPoint;
        private TextHitTestResult? _hit;
        private int _stopIndex = -1;
        private int? _anchor;
        private int? _active;

        public HitsChapter(TourState state) : base(state)
        {
        }

        public override string Title => "Hits";

        public override int StepCount => 5;

        public override string StepName => Step switch
        {
            1 => "point to index",
            2 => "caret stops",
            3 => "inside a ligature",
            4 => "BiDi boundary",
            _ => "selection",
        };

        public override string Caption
        {
            get
            {
                switch (Step)
                {
                    case 1:
                        return _hit is { } hit ?
                            $"TextLayout.HitTestPoint(({_clickPoint!.Value.X - _origin.X:0}, {_clickPoint.Value.Y - _origin.Y:0}))  ->  " +
                            $"CharacterHit({hit.CharacterHit.FirstCharacterIndex}, {hit.CharacterHit.TrailingLength})   flag: ShapedTextRun.BidiLevel" :
                            "TextLayout.HitTestPoint(point)  ->  TextHitTestResult.CharacterHit   (click the text)";
                    case 2 or 3:
                        return CurrentStop is { } stop ?
                            $"TextLine.GetNextCaretCharacterHit  ->  CharacterHit({stop.Hit.FirstCharacterIndex}, {stop.Hit.TrailingLength})" +
                            $"   GetDistanceFromCharacterHit = {stop.X:0.#}   flag: ShapedTextRun.BidiLevel" :
                            "TextLine.GetNextCaretCharacterHit, GetPreviousCaretCharacterHit, GetDistanceFromCharacterHit";
                    case 4:
                        return "TextLine.GetDistanceFromCharacterHit(CharacterHit(i, 0)) vs (CharacterHit(i - 1, 1));  " +
                               "flags: ShapedTextRun.BidiLevel of each hit's run";
                    default:
                        return _anchor is { } a && _active is { } b && a != b ?
                            $"TextLayout.HitTestTextRange({Math.Min(a, b)}, {Math.Abs(b - a)})  ->  {SelectionRects().Count} rectangles" :
                            "TextLayout.HitTestTextRange(start, length)   (drag across the text)";
                }
            }
        }

        private CaretStop? CurrentStop => _stops is { } s && _stopIndex >= 0 && _stopIndex < s.Count ? s[_stopIndex] : null;

        public override int ExportVariantCount(int step) => step == 1 ? 2 : 1;

        public override string ExportVariantName(int step, int variant) => variant == 1 ? "-click" : string.Empty;

        public override void PrepareExport(int step, int variant)
        {
            EnsureLayout();
            var text = State.Text;

            if (step == 1 && variant == 1)
            {
                // A click on the right half of the first right-to-left character shows a leading hit
                // whose index is not the character to the left of the caret.
                var index = FirstRtlIndex();
                var rect = _layout!.HitTestTextPosition(Math.Max(0, index));
                SetClick(new Point(_origin.X + rect.X + rect.Width * 0.75, _origin.Y + rect.Y + rect.Height / 2));
            }
            else if (step is 2 or 3)
            {
                var target = step == 3 ? Math.Max(0, text.IndexOf("ffi", StringComparison.Ordinal)) + 1 : 3;
                _stopIndex = Math.Max(0, _stops!.FindIndex(s => s.Hit.FirstCharacterIndex + s.Hit.TrailingLength >= target));
            }
            else if (step == 5)
            {
                var start = Math.Max(0, text.IndexOf("ffi", StringComparison.Ordinal));
                var rtl = FirstRtlIndex();
                _anchor = start + 1;
                _active = rtl >= 0 ? Math.Min(text.Length, rtl + 8) : Math.Min(text.Length, start + 12);
            }

            RaiseCaptionChanged();
            InvalidateVisual();
        }

        public override bool HandleKey(Key key, KeyModifiers modifiers)
        {
            if (Step is not (2 or 3) || _stops is not { Count: > 0 } stops)
            {
                return false;
            }

            if (key == Key.Right)
            {
                _stopIndex = Math.Min(stops.Count - 1, _stopIndex + 1);
            }
            else if (key == Key.Left)
            {
                _stopIndex = Math.Max(0, _stopIndex - 1);
            }
            else
            {
                return false;
            }

            RaiseCaptionChanged();
            InvalidateVisual();
            return true;
        }

        protected override void InvalidateModel()
        {
            _layout?.Dispose();
            _layout = null;
            _ligatureLayout?.Dispose();
            _ligatureLayout = null;
            _stops = null;

            // A click is a point on the old layout; the selection and the caret stop are text
            // positions and survive a new width.
            _hit = null;
            _clickPoint = null;
            base.InvalidateModel();
        }

        protected override void OnStateChanged(TourChange change)
        {
            if ((change & TourChange.Text) != 0)
            {
                _anchor = _active = null;
                _stopIndex = -1;
            }

            base.OnStateChanged(change);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            EnsureLayout();
            var point = e.GetPosition(this);

            if (Step == 5)
            {
                var position = _layout!.HitTestPoint(point - _origin).TextPosition;
                _anchor = position;
                _active = position;
                e.Pointer.Capture(this);
            }
            else
            {
                SetClick(point);
                var hitPosition = _hit!.Value.CharacterHit.FirstCharacterIndex + _hit.Value.CharacterHit.TrailingLength;
                _stopIndex = _stops!.FindIndex(s => s.Hit.FirstCharacterIndex + s.Hit.TrailingLength == hitPosition);
            }

            RaiseCaptionChanged();
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (Step == 5 && _anchor is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _active = _layout!.HitTestPoint(e.GetPosition(this) - _origin).TextPosition;
                RaiseCaptionChanged();
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        protected override void OnStepChanged()
        {
            if (Step is 2 or 3 && _stopIndex < 0 && _stops is { Count: > 0 })
            {
                _stopIndex = 0;
            }

            InvalidateVisual();
        }

        private void SetClick(Point point)
        {
            _clickPoint = point;
            _hit = _layout!.HitTestPoint(point - _origin);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            EnsureLayout();

            var p = Palette;
            var layout = _layout!;
            var size = 20.0;

            Draw.Text(context, "the string wrapped at the Layout chapter's width", _origin.X, Pad, 16, p.Muted);
            context.DrawRectangle(null, Draw.Pen(p.Grid, 1, dashed: true),
                new Rect(_origin.X, _origin.Y, layout.MaxWidth, layout.Height));

            if (Step == 5)
            {
                foreach (var rect in SelectionRects())
                {
                    context.DrawRectangle(p.Selection, Draw.Pen(p.Accent, 1.5), rect.Translate(_origin));
                }
            }

            layout.Draw(context, _origin);

            var infoTop = _origin.Y + layout.Height + 28;

            switch (Step)
            {
                case 1:
                    RenderClick(context, infoTop, size);
                    break;
                case 2:
                    RenderStops(context, highlightLigature: false);
                    RenderStopInfo(context, infoTop, size);
                    break;
                case 3:
                    RenderStops(context, highlightLigature: true);
                    RenderLigature(context, infoTop, size);
                    break;
                case 4:
                    RenderBidiBoundary(context, infoTop, size);
                    break;
                default:
                    RenderSelectionInfo(context, infoTop, size);
                    break;
            }
        }

        private void RenderClick(DrawingContext context, double top, double size)
        {
            var p = Palette;

            if (_hit is not { } hit || _clickPoint is not { } click)
            {
                Draw.Text(context, "Click anywhere in the text.", _origin.X, top, 24, p.Muted);
                return;
            }

            var cross = Draw.Pen(p.Warning, 2);
            context.DrawLine(cross, click - new Vector(10, 0), click + new Vector(10, 0));
            context.DrawLine(cross, click - new Vector(0, 10), click + new Vector(0, 10));

            var characterHit = hit.CharacterHit;
            var position = characterHit.FirstCharacterIndex + characterHit.TrailingLength;
            var lineIndex = _layout!.GetLineIndexFromCharacterIndex(position, characterHit.TrailingLength > 0);
            DrawCaret(context, lineIndex, characterHit, p.Caret);

            var item = State.Analysis.Items.FirstOrDefault(i => i.Range.Contains(characterHit.FirstCharacterIndex));
            var cluster = State.Analysis.Glyphs.FirstOrDefault(g => g.ClusterRange.Contains(characterHit.FirstCharacterIndex));
            var rows = new List<(string, string)>
            {
                ("CharacterHit", $"FirstCharacterIndex {characterHit.FirstCharacterIndex}, TrailingLength {characterHit.TrailingLength}"),
                ("TextPosition", $"{hit.TextPosition}   IsInside {hit.IsInside}   IsTrailing (past the line end) {hit.IsTrailing}"),
                ("Line", $"{lineIndex}"),
                ("Caret flag", FlagText(lineIndex, characterHit)),
            };

            if (item is not null)
            {
                rows.Add(("Run", $"{item.Script}, BiDi level {item.BidiLevel}, {item.Typeface.FamilyName}"));
            }

            if (cluster.ClusterRange.Length > 0)
            {
                rows.Add(("Cluster", $"text {cluster.ClusterRange.Start}..{cluster.ClusterRange.End - 1} " +
                                     $"'{State.Text.Substring(cluster.ClusterRange.Start, cluster.ClusterRange.Length)}'"));
            }

            DrawInfo(context, top, "Hit test result", rows, size);
        }

        private void RenderStops(DrawingContext context, bool highlightLigature)
        {
            var p = Palette;
            var stops = _stops!;
            var tick = Draw.Pen(p.Accent, 2);

            foreach (var stop in stops)
            {
                var line = _layout!.TextLines[stop.Line];
                var bottom = _origin.Y + stop.LineTop + line.Height;
                var x = _origin.X + stop.X;
                context.DrawLine(tick, new Point(x, bottom - 4), new Point(x, bottom + 10));
            }

            if (CurrentStop is { } current)
            {
                DrawCaret(context, current.Line, current.Hit, p.Caret);
            }
        }

        private void RenderStopInfo(DrawingContext context, double top, double size)
        {
            var rows = new List<(string, string)>
            {
                ("Caret stops", $"{_stops!.Count} for {State.Text.Length} chars, {State.Analysis.Graphemes.Count} graphemes"),
            };

            if (CurrentStop is { } stop)
            {
                rows.Add(("Current", $"CharacterHit({stop.Hit.FirstCharacterIndex}, {stop.Hit.TrailingLength}) on line {stop.Line}"));
                rows.Add(("Distance", $"{stop.X:0.##}"));
                rows.Add(("Caret flag", FlagText(stop.Line, stop.Hit)));
            }

            rows.Add(("Keys", "Left and Right move the caret; PageDown continues"));
            DrawInfo(context, top, "Caret stops", rows, size);
        }

        private void RenderLigature(DrawingContext context, double top, double size)
        {
            var p = Palette;
            var analysis = State.Analysis;
            var ligature = analysis.Clusters().FirstOrDefault(c => c.Glyphs.Count == 1 && c.Range.Length > 1 &&
                                                                   (analysis.Items[c.Glyphs[0].ItemIndex].BidiLevel & 1) == 0 &&
                                                                   !char.IsSurrogate(analysis.Text[c.Range.Start]));

            if (ligature.Glyphs is { Count: 1 })
            {
                var rect = Union(_layout!.HitTestTextRange(ligature.Range.Start, ligature.Range.Length)).Translate(_origin);
                context.DrawRectangle(p.AccentFill, Draw.Pen(p.Accent, 2), rect);
                DrawInfo(context, top, "One glyph, several caret stops", new List<(string, string)>
                {
                    ("Characters", $"'{analysis.Text.Substring(ligature.Range.Start, ligature.Range.Length)}' = {ligature.Range.Length} graphemes"),
                    ("Glyph", $"#{ligature.Glyphs[0].GlyphIndex} in {ligature.Glyphs[0].Typeface.FamilyName}"),
                    ("Stops inside", StopsInside(_stops!.Select(t => t.Hit), ligature.Range.Start, ligature.Range.End)),
                    ("Positions", CaretSource(_layout!, ligature.Range.Start)),
                }, size);
                return;
            }

            // The current font has no ligature in the string: lay out the Latin word again in a
            // family that has one, so the interpolated stops can still be shown.
            var y = top;
            var demoStops = new List<CharacterHit>();
            Rect? selection = null;
            double? cardLeft = null;

            if (_ligatureLayout is { } demo)
            {
                var origin = new Point(_origin.X, y);
                var ffi = demo.TextLines[0];
                // "Office": the ffi ligature starts at index 1.
                const int start = 1;
                var rect = Union(demo.HitTestTextRange(start, 3)).Translate(new Vector(origin.X, origin.Y));
                context.DrawRectangle(p.AccentFill, Draw.Pen(p.Accent, 2), rect);

                // A selection of the second "f" alone ends between two caret stops inside the glyph.
                selection = Union(demo.HitTestTextRange(start + 1, 1));
                context.DrawRectangle(p.SeriesFill(4), Draw.Pen(p.Series(4), 2),
                    selection.Value.Translate(new Vector(origin.X, origin.Y)));
                demo.Draw(context, origin);

                // The demo line's own caret stops, so stops inside the glyph show where the
                // line really puts them.
                var stopHit = new CharacterHit(0);
                var labels = new Dictionary<int, string>();

                for (var guard = 0; guard < 16; guard++)
                {
                    var position = stopHit.FirstCharacterIndex + stopHit.TrailingLength;
                    var x = origin.X + ffi.GetDistanceFromCharacterHit(stopHit);
                    var inLigature = position > start && position < start + 3;
                    context.DrawLine(Draw.Pen(inLigature ? p.Caret : p.Accent, 2), new Point(x, origin.Y + 4),
                        new Point(x, origin.Y + ffi.Height + 10));

                    var key = (int)Math.Round(x);
                    labels[key] = labels.TryGetValue(key, out var existing) ? existing + "," + position : position.ToString();

                    demoStops.Add(stopHit);
                    var next = ffi.GetNextCaretCharacterHit(stopHit);

                    if (next.FirstCharacterIndex + next.TrailingLength <= position)
                    {
                        break;
                    }

                    stopHit = next;
                }

                foreach (var (x, label) in labels)
                {
                    Draw.Text(context, label, x, origin.Y + ffi.Height + 12, 16, p.Caret, HAlign.Center);
                }

                // The card goes beside the demo word so it still fits above the caption on a short screen.
                cardLeft = origin.X + demo.WidthIncludingTrailingWhitespace + 64;
            }

            DrawInfo(context, y, "No ligature in this font", new List<(string, string)>
            {
                ("Font", $"{analysis.Typeface.FontFamily.Name} shapes 'ffi' as {CountFfiGlyphs()} glyphs"),
                ("Shown above", _ligatureFamily is null ? "no installed family with an ffi ligature found" : $"the same word in {_ligatureFamily}: 'ffi' is one glyph"),
                ("Stops inside", _ligatureFamily is null ? "-" : StopsInside(demoStops, 1, 4)),
                ("Positions", _ligatureLayout is null ? "-" : CaretSource(_ligatureLayout, 1)),
                ("Selection", selection is { } s ? $"HitTestTextRange(2, 1)  ->  x {s.X:0.#}  width {s.Width:0.#}, inside the glyph" : "-"),
            }, size, cardLeft);
        }

        /// <summary>Says where the caret stops inside the ligature glyph that starts a cluster come from.</summary>
        private static string CaretSource(TextLayout layout, int clusterStart)
        {
            foreach (var line in layout.TextLines)
            {
                foreach (var run in line.TextRuns.OfType<ShapedTextRun>())
                {
                    foreach (var glyph in run.ShapedBuffer)
                    {
                        if (glyph.GlyphCluster != clusterStart)
                        {
                            continue;
                        }

                        var typeface = run.ShapedBuffer.GlyphTypeface;

                        return typeface.GetLigatureCaretPositions(glyph.GlyphIndex).Length > 0 ?
                            $"the caret list in {typeface.FamilyName}'s GDEF table" :
                            $"the advance divided evenly: {typeface.FamilyName} has no caret list for this glyph";
                    }
                }
            }

            return "-";
        }

        /// <summary>Describes the caret stops strictly inside a cluster, as the line reported them.</summary>
        private static string StopsInside(IEnumerable<CharacterHit> stops, int start, int end)
        {
            var positions = stops.Select(h => h.FirstCharacterIndex + h.TrailingLength).Distinct().OrderBy(x => x).ToList();
            var inside = positions.Where(x => x > start && x < end).ToList();
            var before = positions.LastOrDefault(x => x <= start);
            var after = positions.FirstOrDefault(x => x >= end);

            return inside.Count == 0 ?
                $"none, GetNextCaretCharacterHit goes from {before} to {after}" :
                string.Join(", ", inside);
        }

        private void RenderBidiBoundary(DrawingContext context, double top, double size)
        {
            var p = Palette;
            var analysis = State.Analysis;
            var index = FirstDirectionChange();

            if (index <= 0)
            {
                DrawInfo(context, top, "No direction change in this string", new List<(string, string)>
                {
                    ("Try", "the running example or the Arabic preset"),
                }, size);
                return;
            }

            var lineIndex = _layout!.GetLineIndexFromCharacterIndex(index, false);
            var leading = new CharacterHit(index);
            var trailing = new CharacterHit(index - 1, 1);
            DrawCaret(context, lineIndex, leading, p.Caret, "A");
            DrawCaret(context, _layout.GetLineIndexFromCharacterIndex(index - 1, true), trailing, p.Series(4), "B");

            var line = _layout.TextLines[lineIndex];
            var a = line.GetDistanceFromCharacterHit(leading);
            var b = line.GetDistanceFromCharacterHit(trailing);

            DrawInfo(context, top, $"Text position {index}: two places on screen", new List<(string, string)>
            {
                ("A", $"CharacterHit({index}, 0): before '{analysis.Text[index]}' at x {a:0.#}"),
                ("B", $"CharacterHit({index - 1}, 1): after '{analysis.Text[index - 1]}' at x {b:0.#}"),
                ("HitTestTextPosition", $"{_layout.HitTestTextPosition(index).X:0.#} (one answer for the index)"),
                ("Flag A", FlagText(lineIndex, leading)),
                ("Flag B", FlagText(_layout.GetLineIndexFromCharacterIndex(index - 1, true), trailing)),
            }, size);
        }

        private void RenderSelectionInfo(DrawingContext context, double top, double size)
        {
            var rects = SelectionRects();

            if (rects.Count == 0)
            {
                Draw.Text(context, "Drag across the text, from Latin into the Arabic word.", _origin.X, top, 24, Palette.Muted);
                return;
            }

            var a = Math.Min(_anchor!.Value, _active!.Value);
            var b = Math.Max(_anchor.Value, _active.Value);

            var rows = new List<(string, string)>
            {
                ("Range", $"{a}..{b - 1}  ({b - a} chars)"),
                ("Rectangles", rects.Count.ToString()),
            };

            foreach (var rect in rects.Take(4))
            {
                rows.Add(("", $"x {rect.X:0.#}  width {rect.Width:0.#}  line top {rect.Y:0.#}"));
            }

            DrawInfo(context, top, "HitTestTextRange", rows, size);
        }

        private List<Rect> SelectionRects()
        {
            if (_layout is null || _anchor is not { } a || _active is not { } b || a == b)
            {
                return new List<Rect>();
            }

            return _layout.HitTestTextRange(Math.Min(a, b), Math.Abs(b - a)).ToList();
        }

        private void DrawCaret(DrawingContext context, int lineIndex, CharacterHit hit, IBrush brush, string? label = null)
        {
            var layout = _layout!;
            lineIndex = Math.Clamp(lineIndex, 0, layout.TextLines.Count - 1);
            var top = 0.0;

            for (var i = 0; i < lineIndex; i++)
            {
                top += layout.TextLines[i].Height;
            }

            var line = layout.TextLines[lineIndex];
            var x = _origin.X + line.GetDistanceFromCharacterHit(hit);
            var y = _origin.Y + top;
            context.DrawLine(Draw.Pen(brush, 3), new Point(x, y - 4), new Point(x, y + line.Height + 4));

            // The flag points the way the caret's run reads, to the side the next character comes
            // from: right in a left-to-right run, left in a right-to-left one.
            if (CaretLevel(line, hit) is { } level)
            {
                var sign = (level & 1) == 1 ? -1 : 1;
                var flag = new StreamGeometry();

                using (var g = flag.Open())
                {
                    g.BeginFigure(new Point(x, y - 20), true);
                    g.LineTo(new Point(x + sign * 18, y - 13));
                    g.LineTo(new Point(x, y - 6));
                    g.EndFigure(true);
                }

                context.DrawGeometry(brush, null, flag);
                context.DrawLine(Draw.Pen(brush, 3), new Point(x, y - 21), new Point(x, y - 4));
            }

            if (label is not null)
            {
                Draw.Text(context, label, x, y - 48, 20, brush, HAlign.Center, Draw.UiBold);
            }
        }

        /// <summary>
        /// The BiDi level of the run the caret belongs to: the run of the character the hit names,
        /// which for a trailing hit is the character before the caret.
        /// </summary>
        private static sbyte? CaretLevel(TextLine line, CharacterHit hit) =>
            TextAnalysis.LevelAt(line, hit.FirstCharacterIndex) ??
            TextAnalysis.LevelAt(line, Math.Max(line.FirstTextSourceIndex, hit.FirstCharacterIndex - 1));

        private string FlagText(int lineIndex, CharacterHit hit)
        {
            var line = _layout!.TextLines[Math.Clamp(lineIndex, 0, _layout.TextLines.Count - 1)];

            return CaretLevel(line, hit) is { } level ?
                $"run level {level}: {((level & 1) == 1 ? "right to left, points left" : "left to right, points right")}" :
                "-";
        }

        private void DrawInfo(DrawingContext context, double top, string title, List<(string, string)> rows, double size,
            double? left = null)
        {
            var x = left ?? _origin.X;
            Draw.Card(context, Palette, new Point(x + 10000, top), new Rect(x, top, Math.Max(10, Bounds.Width - x - Pad),
                Math.Max(10, Bounds.Height - top - 8)), title, rows.ToArray(), size);
        }

        private int FirstRtlIndex()
        {
            var analysis = State.Analysis;

            for (var i = 0; i < analysis.Codepoints.Count; i++)
            {
                if ((analysis.Levels[i] & 1) == 1)
                {
                    return analysis.Codepoints[i].Offset;
                }
            }

            return -1;
        }

        // The first code point whose level reads the other way than the code point before it: in
        // the running example the first Arabic letter, in the Arabic preset the number.
        private int FirstDirectionChange()
        {
            var analysis = State.Analysis;

            for (var i = 1; i < analysis.Codepoints.Count; i++)
            {
                if (((analysis.Levels[i] ^ analysis.Levels[i - 1]) & 1) == 1)
                {
                    return analysis.Codepoints[i].Offset;
                }
            }

            return -1;
        }

        private int CountFfiGlyphs()
        {
            var analysis = State.Analysis;
            var start = analysis.Text.IndexOf("ffi", StringComparison.Ordinal);

            return start < 0 ? 0 : analysis.Glyphs.Count(g => g.ClusterRange.Start >= start && g.ClusterRange.Start < start + 3);
        }

        private void EnsureLayout()
        {
            if (_layout is not null)
            {
                return;
            }

            var analysis = State.Analysis;
            var width = Math.Max(200, (Bounds.Width - 2 * Pad) * State.WrapFraction);
            _origin = new Point(Pad, Pad + 70);
            _layout = analysis.CreateLayout(64, width, TextWrapping.Wrap);
            _stops = new List<CaretStop>();

            var top = 0.0;

            for (var lineIndex = 0; lineIndex < _layout.TextLines.Count; lineIndex++)
            {
                var line = _layout.TextLines[lineIndex];
                var hit = new CharacterHit(line.FirstTextSourceIndex);
                var end = line.FirstTextSourceIndex + line.Length - line.NewLineLength;

                for (var guard = 0; guard < 4 * State.Text.Length + 8; guard++)
                {
                    _stops.Add(new CaretStop(lineIndex, top, hit, line.GetDistanceFromCharacterHit(hit)));
                    var next = line.GetNextCaretCharacterHit(hit);

                    if (next == hit || next.FirstCharacterIndex + next.TrailingLength > end ||
                        next.FirstCharacterIndex + next.TrailingLength <= hit.FirstCharacterIndex + hit.TrailingLength)
                    {
                        break;
                    }

                    hit = next;
                }

                top += line.Height;
            }

            if (CountFfiGlyphs() != 1)
            {
                (_ligatureLayout, _ligatureFamily) = CreateLigatureDemo();
            }
        }

        // Tries families that are known to carry an ffi ligature on Windows and macOS and keeps the
        // first one whose shaping really produces a single glyph for it.
        private (TextLayout?, string?) CreateLigatureDemo()
        {
            foreach (var family in SystemFamilies.All("Calibri", "Cambria", "Gabriola", "Hoefler Text", "Baskerville", "Didot", "Georgia", "Palatino"))
            {
                var layout = State.Analysis.CreateLayout(64, double.PositiveInfinity, TextWrapping.NoWrap,
                    typeface: new Typeface(family), text: "Office");

                if (layout.TextLines[0].TextRuns.OfType<ShapedTextRun>().Sum(r => r.ShapedBuffer.Length) == 4)
                {
                    return (layout, family);
                }

                layout.Dispose();
            }

            return (null, null);
        }

        private static Rect Union(IEnumerable<Rect> rects)
        {
            Rect? result = null;

            foreach (var r in rects)
            {
                result = result is { } u ? u.Union(r) : r;
            }

            return result ?? default;
        }

        public override void Dispose()
        {
            _layout?.Dispose();
            _ligatureLayout?.Dispose();
        }

        private sealed record CaretStop(int Line, double LineTop, CharacterHit Hit, double X);
    }
}
