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
    /// The four units of a string, aligned under its characters in logical order: chars (code
    /// units), code points, grapheme clusters and the glyphs shaping produced, each spanning the
    /// units of the row above. Hovering a code point opens its Unicode property card.
    /// </summary>
    internal sealed class UnitsChapter : Chapter
    {
        private const double LabelWidth = 210;

        private readonly GlyphPainter _glyphs = new();
        private readonly List<TextLayout> _graphemeLayouts = new();
        private Model? _model;
        private int _hoverCodepoint = -1;

        public UnitsChapter(TourState state) : base(state)
        {
        }

        public override string Title => "Units";

        public override int StepCount => 5;

        public override string StepName => Step switch
        {
            1 => "UTF-16 code units",
            2 => "code points",
            3 => "grapheme clusters",
            4 => "glyphs",
            _ => "counts",
        };

        public override string Caption => Step switch
        {
            1 => $"string[i] : char      string.Length == {State.Text.Length}",
            2 => "CodepointEnumerator.MoveNext(out Codepoint)  ->  Codepoint.Script, .BiDiClass, ...",
            3 => "GraphemeEnumerator.MoveNext(out Grapheme)  ->  Grapheme.Offset, .Length",
            4 => "ShapedTextRun.ShapedBuffer[i]  ->  GlyphInfo.GlyphIndex, .GlyphCluster",
            _ => "string.Length, CodepointEnumerator, GraphemeEnumerator, ShapedBuffer.Length",
        };

        public override int ExportVariantCount(int step) => step == 2 ? 2 : 1;

        public override string ExportVariantName(int step, int variant) => variant == 1 ? "-card" : string.Empty;

        public override void PrepareExport(int step, int variant)
        {
            _hoverCodepoint = variant == 1 ? DemoCodepoint() : -1;
            InvalidateVisual();
        }

        protected override void InvalidateModel()
        {
            _model = null;
            _hoverCodepoint = -1;
            base.InvalidateModel();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var model = EnsureModel();
            var point = e.GetPosition(this);
            var index = model.Grid.IndexAt(point, s => (s.Top, s.Top + model.CodepointRowBottom));
            var cp = Step >= 2 && index >= 0 ? State.Analysis.CodepointIndexAt(index) : -1;

            if (cp != _hoverCodepoint)
            {
                _hoverCodepoint = cp;
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverCodepoint = -1;
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
            var u = model.Unit;
            var labelSize = Math.Clamp(u * 0.36, 13, 20);
            var text = analysis.Text;

            foreach (var stripe in grid.Stripes)
            {
                DrawRowLabels(context, model, stripe, labelSize);
            }

            // Characters in logical order: each grapheme drawn on its own over the cells it spans.
            for (var g = 0; g < analysis.Graphemes.Count; g++)
            {
                var range = analysis.Graphemes[g];

                foreach (var (stripe, x0, x1) in grid.Spans(range))
                {
                    var box = new Rect(x0, stripe.Top, x1 - x0, model.CharHeight);
                    var layout = _graphemeLayouts[g];
                    layout.Draw(context, new Point(box.X + (box.Width - layout.WidthIncludingTrailingWhitespace) / 2,
                        box.Y + (box.Height - layout.Height) / 2));
                }
            }

            // Row 1: one cell per char with its hex value.
            using (context.PushOpacity(Reveal(1)))
            {
                grid.Band(context, Enumerable.Range(0, text.Length), i => new TextRange(i, 1), model.RowTop(0),
                    model.RowHeight,
                    i => char.IsSurrogate(text[i]) ? p.SeriesFill(1) : p.Surface,
                    i => char.IsSurrogate(text[i]) ? p.Series(1) : p.Grid,
                    i => ((int)text[i]).ToString("X"),
                    labelSize, p.Foreground, Draw.UiMono);
            }

            // Row 2: code points span their code units, a surrogate pair visibly two cells.
            using (context.PushOpacity(Reveal(2)))
            {
                grid.Band(context, analysis.Codepoints.Select((c, i) => (c, i)), t => t.c.Range, model.RowTop(1),
                    model.RowHeight,
                    t => t.i == _hoverCodepoint ? p.AccentFill : t.c.Length > 1 ? p.SeriesFill(1) : p.SeriesFill(0),
                    t => t.i == _hoverCodepoint ? p.Accent : t.c.Length > 1 ? p.Series(1) : p.Series(0),
                    t => t.c.Codepoint.Value.ToString("X"),
                    labelSize, p.Foreground, Draw.UiMono);
            }

            // Row 3: grapheme clusters span their code points.
            using (context.PushOpacity(Reveal(3)))
            {
                grid.Band(context, analysis.Graphemes, r => r, model.RowTop(2), model.RowHeight,
                    r => CodepointCount(analysis, r) > 1 ? p.SeriesFill(3) : p.SeriesFill(2),
                    r => CodepointCount(analysis, r) > 1 ? p.Series(3) : p.Series(2),
                    r => CodepointCount(analysis, r) is var n and > 1 ? $"{n} cp" : string.Empty,
                    labelSize, p.Foreground);
            }

            // Row 4: glyph clusters span their characters and show the glyphs shaping chose.
            using (context.PushOpacity(Reveal(4)))
            {
                foreach (var (range, glyphs) in model.Clusters)
                {
                    var multi = glyphs.Count > 1 || CodepointCount(analysis, range) > 1;

                    foreach (var (stripe, x0, x1) in grid.Spans(range))
                    {
                        var box = new Rect(x0, stripe.Top + model.RowTop(3), x1 - x0, model.GlyphRowHeight);
                        Draw.Segment(context, box, multi ? p.SeriesFill(4) : p.SeriesFill(5),
                            multi ? p.Series(4) : p.Series(5), null, labelSize, p.Foreground);

                        if (stripe == grid.StripeOf(range.Start))
                        {
                            var glyphBox = box.Deflate(new Thickness(3, 2, 3, labelSize * 1.15));
                            DrawClusterGlyphs(context, analysis, glyphs, glyphBox, model.GlyphRowHeight * 0.46);
                            Draw.TextIn(context, string.Join(" ", glyphs.Select(g => $"#{g.GlyphIndex}")),
                                new Rect(box.X, box.Bottom - labelSize * 1.25, box.Width, labelSize * 1.2),
                                labelSize * 0.85, p.Muted, Draw.UiMono);
                        }
                    }
                }
            }

            DrawSummary(context, model, analysis);

            if (_hoverCodepoint >= 0 && _hoverCodepoint < analysis.Codepoints.Count && Step >= 2)
            {
                DrawPropertyCard(context, model, analysis.Codepoints[_hoverCodepoint]);
            }
        }

        private void DrawClusterGlyphs(DrawingContext context, TextAnalysis analysis, IReadOnlyList<GlyphBox> glyphs,
            Rect box, double size)
        {
            _glyphs.DrawCentered(context, analysis.Text, glyphs, box, size, Palette.Foreground);
        }

        private void DrawRowLabels(DrawingContext context, Model model, Stripe stripe, double size)
        {
            var p = Palette;
            var analysis = State.Analysis;
            var x = Pad;
            var counts = Reveal(5);

            void Label(int row, int step, string name, int count, double height)
            {
                using (context.PushOpacity(Reveal(step)))
                {
                    var top = stripe.Top + model.RowTop(row);
                    var box = new Rect(x, top, LabelWidth - 12, height);
                    Draw.TextIn(context, name, box, size, p.Muted, align: HAlign.Left, padding: 0);

                    if (counts > 0 && stripe.Index == 0)
                    {
                        using (context.PushOpacity(counts))
                        {
                            Draw.TextIn(context, count.ToString(), box, size * 1.15, p.Accent, Draw.UiBold, HAlign.Right, 0);
                        }
                    }
                }
            }

            Label(0, 1, "UTF-16 units", analysis.Text.Length, model.RowHeight);
            Label(1, 2, "code points", analysis.Codepoints.Count, model.RowHeight);
            Label(2, 3, "graphemes", analysis.Graphemes.Count, model.RowHeight);
            Label(3, 4, "glyphs", analysis.Glyphs.Count, model.GlyphRowHeight);
        }

        private void DrawSummary(DrawingContext context, Model model, TextAnalysis analysis)
        {
            var reveal = Reveal(5);

            if (reveal <= 0)
            {
                return;
            }

            using (context.PushOpacity(reveal))
            {
                var p = Palette;
                var y = model.Grid.Bottom + model.Unit * 0.5;
                var size = Math.Clamp(model.Unit * 0.5, 18, 28);
                var line = $"string.Length {analysis.Text.Length}    code points {analysis.Codepoints.Count}    " +
                           $"graphemes {analysis.Graphemes.Count}    glyphs {analysis.Glyphs.Count}";
                Draw.Text(context, line, Pad, y, size, p.Foreground, typeface: Draw.UiBold,
                    maxWidth: Bounds.Width - 2 * Pad);
            }
        }

        private void DrawPropertyCard(DrawingContext context, Model model, CodepointInfo info)
        {
            var cp = info.Codepoint;
            var span = model.Grid.Spans(info.Range).FirstOrDefault();

            if (span.Stripe is null)
            {
                return;
            }

            var anchor = new Point((span.X0 + span.X1) / 2, span.Stripe.Top + model.RowTop(2));
            var rows = new List<(string, string)>
            {
                ("General category", cp.GeneralCategory.ToString()),
                ("Script", cp.Script.ToString()),
                ("BiDi class", cp.BiDiClass.ToString()),
                ("Line break class", cp.LineBreakClass.ToString()),
                ("Grapheme break class", cp.GraphemeBreakClass.ToString()),
                ("Word break class", cp.WordBreakClass.ToString()),
                ("East Asian width", cp.EastAsianWidthClass.ToString()),
                ("Emoji", cp.IsEmoji ? cp.HasEmojiPresentation ? "yes, emoji presentation" : "yes, text presentation" : "no"),
                ("UTF-16", info.Length == 2 ? $"{(int)State.Text[info.Offset]:X4} {(int)State.Text[info.Offset + 1]:X4}" : $"{(int)State.Text[info.Offset]:X4}"),
            };

            if (cp.IsDefaultIgnorable)
            {
                rows.Add(("Default ignorable", "yes"));
            }

            var glyph = char.IsControl(State.Text[info.Offset]) || cp.IsDefaultIgnorable ?
                string.Empty :
                "  " + char.ConvertFromUtf32((int)cp.Value);
            Draw.Card(context, Palette, anchor, new Rect(Bounds.Size).Deflate(8), $"{Draw.Hex(cp.Value)}{glyph}",
                rows.ToArray(), Math.Clamp(model.Unit * 0.36, 15, 20));
        }

        private int DemoCodepoint()
        {
            var cps = State.Analysis.Codepoints;

            for (var i = 0; i < cps.Count; i++)
            {
                if (cps[i].Codepoint.BiDiClass is BidiClass.ArabicLetter or BidiClass.RightToLeft)
                {
                    return i;
                }
            }

            for (var i = 0; i < cps.Count; i++)
            {
                if (cps[i].Codepoint.Value > 0x7F)
                {
                    return i;
                }
            }

            return cps.Count > 0 ? 0 : -1;
        }

        private static int CodepointCount(TextAnalysis analysis, TextRange range)
        {
            var n = 0;

            foreach (var cp in analysis.Codepoints)
            {
                if (range.Contains(cp.Offset))
                {
                    n++;
                }
            }

            return n;
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

            // The largest unit that fits: rows shrink before the grid wraps into more stripes than fit.
            for (var u = 60.0; u >= 26; u -= 2)
            {
                chosenCell = LogicalGrid.FitCellWidth(analysis.Text.Length, width, Math.Max(34, u * 0.82), u * 1.5);
                var candidate = new Model(u, analysis, Pad + LabelWidth, Pad, width, chosenCell);

                model = candidate;

                if (candidate.Grid.Bottom + u * 1.4 <= Pad + height)
                {
                    break;
                }
            }

            // Spare height goes to the rows, so a wide display does not leave the bottom half empty.
            var stretch = Math.Clamp(height / Math.Max(1, model!.Grid.Bottom + model.Unit * 1.4 - Pad), 1, 1.4);

            if (stretch > 1.01)
            {
                model = new Model(model.Unit, analysis, Pad + LabelWidth, Pad, width, chosenCell, stretch);
            }

            foreach (var g in analysis.Graphemes)
            {
                _graphemeLayouts.Add(new TextLayout(analysis.Text.Substring(g.Start, g.Length), State.Typeface,
                    Math.Min(model.CharHeight * 0.66, model.Grid.CellWidth * 0.95), Palette.Foreground));
            }

            return _model = model!;
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

        /// <summary>Row geometry of the units table for one unit size.</summary>
        private sealed class Model
        {
            public Model(double unit, TextAnalysis analysis, double left, double top, double width, double cell,
                double stretch = 1)
            {
                var v = unit * stretch;
                Unit = unit;
                CharHeight = v * 1.45;
                RowHeight = v * 0.78;
                GlyphRowHeight = v * 1.45;
                Gap = v * 0.16;

                var stripeHeight = CharHeight + Gap + 3 * (RowHeight + Gap) + GlyphRowHeight;
                Grid = new LogicalGrid(analysis, left, top, width, cell, stripeHeight, unit * 0.55, unit * 1.5);
                Clusters = analysis.Clusters();
            }

            public double Unit { get; }

            public double CharHeight { get; }

            public double RowHeight { get; }

            public double GlyphRowHeight { get; }

            public double Gap { get; }

            public LogicalGrid Grid { get; }

            public IReadOnlyList<(TextRange Range, IReadOnlyList<GlyphBox> Glyphs)> Clusters { get; }

            /// <summary>The bottom of the code point row, relative to the stripe top.</summary>
            public double CodepointRowBottom => RowTop(1) + RowHeight;

            /// <summary>The top of table row <paramref name="row"/> (0 = code units), relative to the stripe top.</summary>
            public double RowTop(int row) => CharHeight + Gap + row * (RowHeight + Gap);
        }
    }
}
