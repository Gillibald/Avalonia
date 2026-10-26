using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;

namespace TextTour.Chapters
{
    /// <summary>
    /// The layers the formatter cuts the string into, stacked under its characters in logical
    /// order: runs from the text source, graphemes, words, break opportunities, BiDi levels and the
    /// shaped items. The last step adds the glyphs in visual order above the characters, each
    /// connected to the characters of its cluster.
    /// </summary>
    internal sealed class TokensChapter : Chapter
    {
        private const double LabelWidth = 170;

        private readonly GlyphPainter _glyphs = new();
        private readonly List<TextLayout> _graphemeLayouts = new();
        private Model? _model;
        private int _hoverGlyph = -1;

        public TokensChapter(TourState state) : base(state)
        {
        }

        public override string Title => "Tokens";

        public override int StepCount => 7;

        public override string StepName => Step switch
        {
            1 => "runs from the source",
            2 => "graphemes",
            3 => "words",
            4 => "break opportunities",
            5 => "BiDi levels",
            6 => "items",
            _ => "glyphs",
        };

        public override string Caption => Step switch
        {
            1 => "ITextSource.GetTextRun(index)  ->  TextCharacters, TextRunProperties",
            2 => "GraphemeEnumerator.MoveNext(out Grapheme)",
            3 => "WordBreakEnumerator.MoveNext(out WordSegment)",
            4 => "LineBreakEnumerator.MoveNext(out LineBreak)  ->  PositionWrap, Required",
            5 => "BidiAlgorithm.Process(BidiData)  ->  ResolvedLevels, ResolvedParagraphEmbeddingLevel;  " +
                 "GenericTextParagraphProperties.FlowDirection",
            6 => "TextLine.TextRuns  ->  ShapedTextRun.BidiLevel (arrow: odd = right to left), .Properties.Typeface, " +
                 ".ShapedBuffer.GlyphTypeface",
            _ => "ShapedBuffer[i]  ->  GlyphInfo(GlyphIndex, GlyphCluster, GlyphAdvance, GlyphOffset)",
        };

        public override int ExportVariantCount(int step) => step == 7 ? 2 : 1;

        public override string ExportVariantName(int step, int variant) => variant == 1 ? "-card" : string.Empty;

        public override void PrepareExport(int step, int variant)
        {
            _hoverGlyph = variant == 1 ? DemoGlyph() : -1;
            InvalidateVisual();
        }

        protected override void InvalidateModel()
        {
            _model = null;
            _hoverGlyph = -1;
            base.InvalidateModel();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (Step < 7)
            {
                return;
            }

            var model = EnsureModel();
            var point = e.GetPosition(this);
            var hit = -1;

            foreach (var (index, rect) in model.GlyphBoxes)
            {
                if (rect.Inflate(new Thickness(0, 0, 0, model.Unit * 0.5)).Contains(point))
                {
                    hit = index;
                    break;
                }
            }

            if (hit != _hoverGlyph)
            {
                _hoverGlyph = hit;
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverGlyph = -1;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            _glyphs.Clear();

            var model = EnsureModel();
            var analysis = State.Analysis;
            var p = Palette;
            var grid = model.Grid;
            var labelSize = Math.Clamp(model.Unit * 0.5, 13, 20);

            foreach (var stripe in grid.Stripes)
            {
                DrawLabels(context, model, stripe, labelSize);
            }

            // Characters in logical order; the styled span of the text source keeps its own brush.
            for (var g = 0; g < analysis.Graphemes.Count; g++)
            {
                foreach (var (stripe, x0, x1) in grid.Spans(analysis.Graphemes[g]))
                {
                    var layout = _graphemeLayouts[g];
                    layout.Draw(context, new Point((x0 + x1 - layout.WidthIncludingTrailingWhitespace) / 2,
                        stripe.Top + model.CharTop + (model.CharHeight - layout.Height) / 2));
                }
            }

            using (context.PushOpacity(Reveal(1)))
            {
                grid.Band(context, analysis.SourceRuns.Select((r, i) => (r, i)), t => t.r.Range, model.BandTop(0),
                    model.RowHeight,
                    t => t.r.IsStyled ? p.SeriesFill(1) : p.Surface,
                    t => t.r.IsStyled ? p.Series(1) : p.Grid,
                    t => $"run {t.i + 1}" + (t.r.IsStyled ? ": own brush" : string.Empty),
                    labelSize, p.Foreground);
            }

            using (context.PushOpacity(Reveal(2)))
            {
                grid.Band(context, analysis.Graphemes, r => r, model.BandTop(1), model.RowHeight,
                    r => r.Length > 1 ? p.SeriesFill(3) : p.SeriesFill(2),
                    r => r.Length > 1 ? p.Series(3) : p.Series(2),
                    _ => null, labelSize, p.Foreground);
            }

            using (context.PushOpacity(Reveal(3)))
            {
                grid.Band(context, analysis.Words.Select((w, i) => (w, i)), t => t.w.Length > 0 ? t.w : default,
                    model.BandTop(2), model.RowHeight,
                    t => IsSpace(analysis, t.w) ? null : p.SeriesFill(t.i % 2 == 0 ? 0 : 5),
                    t => IsSpace(analysis, t.w) ? p.Grid : p.Series(t.i % 2 == 0 ? 0 : 5),
                    _ => null, labelSize, p.Foreground);
            }

            using (context.PushOpacity(Reveal(4)))
            {
                DrawBreaks(context, model, analysis);
            }

            using (context.PushOpacity(Reveal(5)))
            {
                grid.Band(context, analysis.Codepoints.Select((c, i) => (c, Level: analysis.Levels[i])),
                    t => t.c.Range, model.BandTop(4), model.RowHeight,
                    t => t.Level == 0 ? p.Surface : p.SeriesFill(t.Level + 2),
                    t => t.Level == 0 ? p.Grid : p.Series(t.Level + 2),
                    t => t.Level.ToString(), labelSize, p.Foreground, Draw.UiBold);
                DrawLevelLegend(context, model, labelSize);
                DrawParagraphBadge(context, model, analysis, labelSize);
            }

            using (context.PushOpacity(Reveal(6)))
            {
                foreach (var item in analysis.Items)
                {
                    foreach (var (stripe, x0, x1) in grid.Spans(item.Range))
                    {
                        var box = new Rect(x0, stripe.Top + model.BandTop(6), x1 - x0, model.ItemHeight);
                        Draw.Segment(context, box, p.SeriesFill(item.Index), p.Series(item.Index), null, labelSize,
                            p.Foreground);
                        var row1 = box.Height * 0.37;
                        var row2 = box.Height * 0.31;
                        Draw.TextIn(context, $"{item.Script}  L{item.BidiLevel}", new Rect(box.X, box.Y + 1, box.Width, row1),
                            labelSize * 0.92, p.Foreground, Draw.UiBold);
                        Draw.TextIn(context, item.Typeface.FamilyName, new Rect(box.X, box.Y + row1, box.Width, row2),
                            labelSize * 0.85, p.Foreground);

                        // The run's reading direction: an odd level reads right to left.
                        var arrowRow = new Rect(box.X + 1.5, box.Y + row1 + row2, box.Width - 3, box.Height - row1 - row2 - 2);
                        Draw.DirectionArrow(context, arrowRow, (item.BidiLevel & 1) == 1, p.Foreground, labelSize * 2.6,
                            Math.Max(2, labelSize * 0.16));
                    }
                }
            }

            using (context.PushOpacity(Reveal(7)))
            {
                DrawGlyphs(context, model, analysis, labelSize);
            }

            if (_hoverGlyph >= 0 && Step >= 7)
            {
                DrawGlyphCard(context, model, analysis, labelSize);
            }
        }

        private void DrawLabels(DrawingContext context, Model model, Stripe stripe, double size)
        {
            var p = Palette;
            var names = new[] { "runs", "graphemes", "words", "breaks", "BiDi levels", "items" };

            for (var i = 0; i < names.Length; i++)
            {
                using (context.PushOpacity(Reveal(i + 1)))
                {
                    // The legend row under the levels sits between band 4 and the items.
                    var band = i == 5 ? 6 : i;
                    var height = i == 5 ? model.ItemHeight : i == 3 ? model.BreakHeight : model.RowHeight;
                    Draw.TextIn(context, names[i], new Rect(Pad, stripe.Top + model.BandTop(band), LabelWidth - 10, height),
                        size, p.Muted, align: HAlign.Left, padding: 0);
                }
            }

            using (context.PushOpacity(Reveal(7)))
            {
                Draw.TextIn(context, "glyphs", new Rect(Pad, stripe.Top, LabelWidth - 10, model.GlyphHeight), size,
                    p.Muted, align: HAlign.Left, padding: 0);
                Draw.TextIn(context, "visual order", new Rect(Pad, stripe.Top + model.GlyphHeight, LabelWidth - 10, size * 1.4),
                    size * 0.8, p.Muted, align: HAlign.Left, padding: 0);
            }
        }

        // The meaning of the levels as directions, once under the levels band of the first stripe.
        private void DrawLevelLegend(DrawingContext context, Model model, double size)
        {
            var p = Palette;
            var stripe = model.Grid.Stripes[0];
            var row = new Rect(model.Grid.Left, stripe.Top + model.BandTop(5), 0, model.LegendHeight);
            var textSize = size * 0.92;
            var arrowLength = textSize * 2.2;
            var thickness = Math.Max(2, size * 0.15);
            var x = row.X + 4;

            foreach (var (rtl, text) in new[] { (false, "even level: left to right"), (true, "odd level: right to left") })
            {
                Draw.DirectionArrow(context, new Rect(x, row.Y, arrowLength + 8, row.Height), rtl, p.Foreground,
                    arrowLength, thickness);
                x += arrowLength + 14;
                using var layout = Draw.Layout(text, textSize, p.Foreground);
                layout.Draw(context, new Point(x, row.Y + (row.Height - layout.Height) / 2));
                x += layout.WidthIncludingTrailingWhitespace + textSize * 2;
            }
        }

        // The paragraph direction the levels were resolved in, beside the characters of the first stripe.
        private void DrawParagraphBadge(DrawingContext context, Model model, TextAnalysis analysis, double size)
        {
            var stripe = model.Grid.Stripes[0];

            // Two lines of the badge have to fit the character row, which is short on a small screen.
            var badgeSize = Math.Clamp(model.CharHeight / 2.5, 11, size * 0.95);
            var estimate = badgeSize * 2.3;
            Draw.Badge(context, Palette, Pad, stripe.Top + model.CharTop + Math.Max(0, (model.CharHeight - estimate) / 2),
                $"paragraph: {Draw.Direction(analysis.ParagraphFlowDirection)}", badgeSize,
                $"level {analysis.ParagraphLevel}", LabelWidth - 14);
        }

        private void DrawBreaks(DrawingContext context, Model model, TextAnalysis analysis)
        {
            var p = Palette;
            var grid = model.Grid;

            foreach (var stripe in grid.Stripes)
            {
                var top = stripe.Top + model.BandTop(3);
                var mid = top + model.BreakHeight / 2;
                context.DrawLine(Draw.Pen(p.Grid, 1.5), new Point(grid.X(stripe, stripe.Range.Start), mid),
                    new Point(grid.X(stripe, stripe.Range.End), mid));
            }

            foreach (var lineBreak in analysis.Breaks)
            {
                var position = lineBreak.PositionWrap;
                var stripe = grid.StripeOf(position) ?? grid.Stripes[^1];

                // A break position on a stripe boundary is drawn at the end of the earlier stripe.
                if (position == stripe.Range.Start && stripe.Index > 0)
                {
                    stripe = grid.Stripes[stripe.Index - 1];
                }

                var x = grid.X(stripe, position);
                var top = stripe.Top + model.BandTop(3);
                var pen = lineBreak.Required ? Draw.Pen(p.Warning, 3.5) : Draw.Pen(p.Accent, 2.5, dashed: true);
                context.DrawLine(pen, new Point(x, top), new Point(x, top + model.BreakHeight));
            }
        }

        private void DrawGlyphs(DrawingContext context, Model model, TextAnalysis analysis, double labelSize)
        {
            var p = Palette;
            var connector = Draw.Pen(p.Muted, 1.5);
            var identity = Draw.Pen(p.Caret, 2, dashed: true);

            foreach (var (index, rect) in model.GlyphBoxes)
            {
                var glyph = analysis.Glyphs[index];
                var hovered = index == _hoverGlyph;
                var item = analysis.Items[glyph.ItemIndex];
                context.DrawRectangle(hovered ? p.AccentFill : p.SeriesFill(item.Index),
                    Draw.Pen(hovered ? p.Accent : p.Series(item.Index), hovered ? 2.5 : 1.5), rect.Deflate(new Thickness(1, 0)), 3, 3);

                var shape = rect.Deflate(new Thickness(1, 2, 1, labelSize * 1.1));
                _glyphs.DrawCentered(context, analysis.Text, new[] { glyph with { Offset = default } }, shape,
                    model.GlyphHeight * 0.63, p.Foreground);
                Draw.TextIn(context, glyph.GlyphIndex.ToString(),
                    new Rect(rect.X, rect.Bottom - labelSize * 1.15, rect.Width, labelSize * 1.1),
                    labelSize * 0.75, p.Muted, Draw.UiMono, padding: 1);

                var stripe = model.Grid.StripeOf(glyph.ClusterRange.Start);

                if (stripe is null)
                {
                    continue;
                }

                var from = new Point(rect.Center.X, rect.Bottom);
                var span = model.Grid.Spans(glyph.ClusterRange).First();
                var to = new Point((span.X0 + span.X1) / 2, stripe.Top + model.CharTop);
                Draw.Connector(context, hovered ? Draw.Pen(p.Accent, 2.5) : connector, from, to);

                // Inside a merged cluster, a glyph that is the font's own glyph for one of the
                // cluster's code points (by cmap) is also tied to that code point: this shows
                // where shaping moved a character, such as a vowel sign drawn before its consonant.
                if (CodepointsOf(analysis, glyph.ClusterRange) is { Count: > 1 } cps)
                {
                    foreach (var cp in cps)
                    {
                        if (glyph.Typeface.CharacterToGlyphMap.TryGetGlyph((int)cp.Codepoint.Value, out var id) &&
                            id == glyph.GlyphIndex)
                        {
                            var cell = model.Grid.Spans(cp.Range).First();
                            Draw.Connector(context, identity, from,
                                new Point((cell.X0 + cell.X1) / 2, stripe.Top + model.CharTop));
                        }
                    }
                }
            }
        }

        private void DrawGlyphCard(DrawingContext context, Model model, TextAnalysis analysis, double size)
        {
            var box = model.GlyphBoxes.FirstOrDefault(b => b.Index == _hoverGlyph);

            if (box.Rect.Width <= 0)
            {
                return;
            }

            var glyph = analysis.Glyphs[_hoverGlyph];
            var chars = analysis.Text.Substring(glyph.ClusterRange.Start, glyph.ClusterRange.Length);
            var cps = string.Join(" ", CodepointsOf(analysis, glyph.ClusterRange).Select(c => Draw.Hex(c.Codepoint.Value)));
            var rows = new[]
            {
                ("Glyph index", glyph.GlyphIndex.ToString()),
                ("Cluster", $"{glyph.Cluster} (text {glyph.ClusterRange.Start}..{glyph.ClusterRange.End - 1})"),
                ("Advance", $"{glyph.Advance:0.##}"),
                ("Offset", $"{glyph.Offset.X:0.##}, {glyph.Offset.Y:0.##}"),
                ("Characters", cps),
                ("Font", glyph.Typeface.FamilyName),
            };

            Draw.Card(context, Palette, new Point(box.Rect.Center.X, box.Rect.Bottom + 8),
                new Rect(Bounds.Size).Deflate(8), $"Glyph {glyph.GlyphIndex}  \u2068{chars}\u2069", rows, Math.Max(15, size));
        }

        private int DemoGlyph()
        {
            var analysis = State.Analysis;

            // Prefer a glyph that shaping moved inside a merged cluster, then a right-to-left glyph.
            for (var i = 0; i < analysis.Glyphs.Count; i++)
            {
                var g = analysis.Glyphs[i];

                if (CodepointsOf(analysis, g.ClusterRange).Count > 2 && analysis.Items[g.ItemIndex].Script != Script.Common)
                {
                    return i;
                }
            }

            for (var i = 0; i < analysis.Glyphs.Count; i++)
            {
                if ((analysis.Items[analysis.Glyphs[i].ItemIndex].BidiLevel & 1) == 1)
                {
                    return i;
                }
            }

            return analysis.Glyphs.Count > 0 ? 0 : -1;
        }

        private static List<CodepointInfo> CodepointsOf(TextAnalysis analysis, TextRange range) =>
            analysis.Codepoints.Where(c => range.Contains(c.Offset)).ToList();

        private static bool IsSpace(TextAnalysis analysis, TextRange range)
        {
            for (var i = range.Start; i < range.End; i++)
            {
                if (!char.IsWhiteSpace(analysis.Text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private Model EnsureModel()
        {
            if (_model is { } existing)
            {
                return existing;
            }

            foreach (var layout in _graphemeLayouts)
            {
                layout.Dispose();
            }

            _graphemeLayouts.Clear();

            var analysis = State.Analysis;
            var width = Math.Max(200, Bounds.Width - 2 * Pad - LabelWidth);
            var height = Math.Max(200, Bounds.Height - 2 * Pad);
            Model? model = null;
            var chosenCell = 0.0;

            for (var u = 56.0; u >= 22; u -= 2)
            {
                chosenCell = LogicalGrid.FitCellWidth(analysis.Text.Length, width, Math.Max(24, u * 0.8), u * 1.4);
                model = new Model(u, analysis, Pad + LabelWidth, Pad, width, chosenCell);

                if (model.Grid.Bottom <= Pad + height)
                {
                    break;
                }
            }

            // Spare height goes to the rows, so a wide display does not leave the bottom half empty.
            var stretch = Math.Clamp(height / Math.Max(1, model!.Grid.Bottom - Pad), 1, 1.45);

            if (stretch > 1.01)
            {
                model = new Model(model.Unit, analysis, Pad + LabelWidth, Pad, width, chosenCell, stretch);
            }

            var span = State.StyleSpan;

            foreach (var g in analysis.Graphemes)
            {
                var styled = span is { } s && g.Start >= s.Start && g.Start < s.Start + s.Length;
                _graphemeLayouts.Add(new TextLayout(analysis.Text.Substring(g.Start, g.Length), State.Typeface,
                    Math.Min(model.CharHeight * 0.66, model.Grid.CellWidth * 0.95), styled ? Palette.Series(1) : Palette.Foreground));
            }

            model!.PlaceGlyphs(analysis);

            return _model = model;
        }

        public override void Dispose()
        {
            _glyphs.Dispose();

            foreach (var layout in _graphemeLayouts)
            {
                layout.Dispose();
            }

            _graphemeLayouts.Clear();
        }

        /// <summary>Row geometry of the layer stack for one unit size.</summary>
        private sealed class Model
        {
            private readonly double[] _bandTops = new double[7];
            private readonly List<(int Index, Rect Rect)> _glyphBoxes = new();

            public Model(double unit, TextAnalysis analysis, double left, double top, double width, double cell,
                double stretch = 1)
            {
                var v = unit * stretch;
                Unit = unit;
                GlyphHeight = v * 1.5;
                CharTop = GlyphHeight + v * 1.5;
                CharHeight = v * 1.3;
                RowHeight = v * 0.72;
                BreakHeight = v * 0.6;
                ItemHeight = v * 1.75;
                LegendHeight = Math.Max(v * 0.55, 18);

                var gap = v * 0.14;
                var y = CharTop + CharHeight + gap;

                // Bands 0-4 are the runs, graphemes, words, breaks and levels; 5 is the legend of
                // the levels and 6 the items.
                for (var i = 0; i < 7; i++)
                {
                    _bandTops[i] = y;
                    y += (i switch { 3 => BreakHeight, 5 => LegendHeight, 6 => ItemHeight, _ => RowHeight }) + gap;
                }

                Grid = new LogicalGrid(analysis, left, top, width, cell, y - gap, unit * 0.6, unit * 1.4);
            }

            public double Unit { get; }

            public double GlyphHeight { get; }

            public double CharTop { get; }

            public double CharHeight { get; }

            public double RowHeight { get; }

            public double BreakHeight { get; }

            public double ItemHeight { get; }

            public double LegendHeight { get; }

            public LogicalGrid Grid { get; }

            /// <summary>Glyph boxes by index into <see cref="TextAnalysis.Glyphs"/>.</summary>
            public IReadOnlyList<(int Index, Rect Rect)> GlyphBoxes => _glyphBoxes;

            public double BandTop(int band) => _bandTops[band];

            /// <summary>
            /// Places each stripe's glyphs in visual order with their real advances, scaled to the
            /// stripe width; zero-advance glyphs (marks, joiners) get a narrow box so they stay visible.
            /// </summary>
            public void PlaceGlyphs(TextAnalysis analysis)
            {
                var minWidth = Unit * 0.35;

                foreach (var stripe in Grid.Stripes)
                {
                    var members = analysis.Glyphs
                        .Select((g, i) => (g, i))
                        .Where(t => stripe.Range.Contains(t.g.ClusterRange.Start))
                        .ToList();

                    if (members.Count == 0)
                    {
                        continue;
                    }

                    var stripeWidth = stripe.Range.Length * Grid.CellWidth;
                    var advance = members.Sum(t => t.g.Advance);
                    var fixedWidth = members.Count(t => t.g.Advance <= 0.01) * minWidth;
                    var scale = advance > 0 ? Math.Min(2.5, Math.Max(0.1, (stripeWidth - fixedWidth) / advance)) : 1;
                    var total = members.Sum(t => Math.Max(minWidth, t.g.Advance * scale));
                    var x = Grid.Left + Math.Max(0, (stripeWidth - total) / 2);

                    foreach (var (g, i) in members)
                    {
                        var w = Math.Max(minWidth, g.Advance * scale);
                        _glyphBoxes.Add((i, new Rect(x, stripe.Top, w, GlyphHeight)));
                        x += w;
                    }
                }
            }
        }
    }
}
