using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour.Chapters
{
    /// <summary>
    /// The running example in a plain <see cref="TextBlock"/>, then the question of what happens
    /// between the string and the pixels, answered by the chapter names.
    /// </summary>
    internal sealed class StartChapter : Chapter
    {
        private static readonly (string Name, string Line)[] s_stages =
        {
            ("Units", "chars, code points, graphemes, glyphs"),
            ("Fonts", "a family name is a query; fallback per character"),
            ("Tokens", "runs, segmentation, BiDi, items, shaping"),
            ("Layout", "lines, breaks, visual order, metrics"),
            ("Hits", "points to text positions and back"),
        };

        private readonly TextBlock _text;

        public StartChapter(TourState state) : base(state)
        {
            _text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(Pad * 2, Pad * 2, Pad * 2, 0),
            };
            Child = _text;
            UpdateText();
        }

        public override string Title => "Start";

        public override int StepCount => 2;

        public override string StepName => Step == 1 ? "the running example" : "the map";

        public override string Caption => Step == 1 ?
            $"<TextBlock Text=\"...\" FontFamily=\"{State.FamilyList}\" FontSize=\"72\" />" :
            "TextBlock  ->  TextLayout  ->  TextFormatter  ->  TextLine  ->  GlyphRun";

        protected override void OnStateChanged(TourChange change)
        {
            UpdateText();
            base.OnStateChanged(change);
        }

        private void UpdateText()
        {
            _text.Text = State.Text;
            _text.FontFamily = State.Typeface.FontFamily;
            _text.FontWeight = State.Weight;
            _text.FontStyle = State.Style;
            _text.FontSize = 72;
            _text.Foreground = Palette.Foreground;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var reveal = Reveal(2);

            if (reveal <= 0)
            {
                return;
            }

            using (context.PushOpacity(reveal))
            {
                var p = Palette;
                var top = Math.Max(_text.Bounds.Bottom + 40, Bounds.Height * 0.36);
                Draw.Text(context, "What happens between this string and the pixels, and back to a caret?",
                    Bounds.Width / 2, top, 28, p.Foreground, HAlign.Center, Draw.UiBold, Bounds.Width - 2 * Pad);

                var count = s_stages.Length;
                var gap = 28.0;
                var width = Math.Min(260, (Bounds.Width - 2 * Pad - (count - 1) * gap) / count);
                var total = count * width + (count - 1) * gap;
                var x = (Bounds.Width - total) / 2;
                var y = top + 70;
                var height = 0.0;

                foreach (var stage in s_stages)
                {
                    using var probe = new TextLayout(stage.Line, Draw.UiTypeface, 19, p.Foreground, TextAlignment.Center,
                        TextWrapping.Wrap, maxWidth: width - 24);
                    height = Math.Max(height, 76 + probe.Height + 16);
                }

                for (var i = 0; i < count; i++)
                {
                    var box = new Rect(x + i * (width + gap), y, width, height);
                    context.DrawRectangle(p.SeriesFill(i), Draw.Pen(p.Series(i), 2.5), box, 10, 10);
                    Draw.TextIn(context, $"{i + 2}  {s_stages[i].Name}", box.WithHeight(64).Translate(new Vector(0, 8)), 30, p.Foreground,
                        Draw.UiBold);
                    using var line = new TextLayout(s_stages[i].Line, Draw.UiTypeface, 19, p.Foreground, TextAlignment.Center,
                        TextWrapping.Wrap, maxWidth: width - 24);
                    line.Draw(context, new Point(box.X + 12, box.Y + 76));

                    if (i < count - 1)
                    {
                        var ax = box.Right + 4;
                        var ay = box.Center.Y;
                        var pen = Draw.Pen(p.Muted, 3);
                        context.DrawLine(pen, new Point(ax, ay), new Point(ax + gap - 8, ay));
                        context.DrawLine(pen, new Point(ax + gap - 16, ay - 7), new Point(ax + gap - 8, ay));
                        context.DrawLine(pen, new Point(ax + gap - 16, ay + 7), new Point(ax + gap - 8, ay));
                    }
                }

                Draw.Text(context, "Keys: PageDown, Space or Right for the next step; 1-6 for a chapter; T theme; F full screen; +/- scale",
                    Bounds.Width / 2, y + height + 40, 18, p.Muted, HAlign.Center, maxWidth: Bounds.Width - 2 * Pad);
            }
        }
    }
}
