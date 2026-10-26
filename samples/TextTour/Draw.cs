using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour
{
    internal enum HAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>Drawing helpers shared by the chapters: labels in the UI font, boxes and connectors.</summary>
    internal static class Draw
    {
        /// <summary>The UI font for labels: the platform default family.</summary>
        public static Typeface UiTypeface { get; } = new(FontFamily.Default);

        public static Typeface UiBold { get; } = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        public static Typeface UiMono { get; } = new(new FontFamily("Cascadia Mono, Consolas, Menlo, SF Mono, monospace, $Default"));

        /// <summary>Lays out a label once; the caller draws it and disposes it.</summary>
        public static TextLayout Layout(string text, double size, IBrush brush, Typeface? typeface = null,
            double maxWidth = double.PositiveInfinity, TextTrimming? trimming = null)
        {
            return new TextLayout(text, typeface ?? UiTypeface, size, brush, maxWidth: maxWidth,
                textTrimming: trimming ?? (double.IsInfinity(maxWidth) ? TextTrimming.None : TextTrimming.CharacterEllipsis));
        }

        /// <summary>Draws a single line label with its top at <paramref name="y"/>, aligned around <paramref name="x"/>.</summary>
        public static Size Text(DrawingContext context, string text, double x, double y, double size, IBrush brush,
            HAlign align = HAlign.Left, Typeface? typeface = null, double maxWidth = double.PositiveInfinity)
        {
            using var layout = Layout(text, size, brush, typeface, maxWidth);
            var left = align switch
            {
                HAlign.Center => x - layout.WidthIncludingTrailingWhitespace / 2,
                HAlign.Right => x - layout.WidthIncludingTrailingWhitespace,
                _ => x,
            };

            layout.Draw(context, new Point(left, y));

            return new Size(layout.WidthIncludingTrailingWhitespace, layout.Height);
        }

        /// <summary>Draws a label vertically centred in <paramref name="box"/>, shrunk to fit its width if needed.</summary>
        public static void TextIn(DrawingContext context, string text, Rect box, double size, IBrush brush,
            Typeface? typeface = null, HAlign align = HAlign.Center, double padding = 4)
        {
            using var probe = Layout(text, size, brush, typeface);
            var available = Math.Max(1, box.Width - 2 * padding);
            var fitted = probe.WidthIncludingTrailingWhitespace > available ?
                Math.Max(size * 0.62, size * available / probe.WidthIncludingTrailingWhitespace) :
                size;

            using var layout = Layout(text, fitted, brush, typeface, available + 0.5);
            var y = box.Y + (box.Height - layout.Height) / 2;
            var x = align switch
            {
                HAlign.Left => box.X + padding,
                HAlign.Right => box.Right - padding - layout.WidthIncludingTrailingWhitespace,
                _ => box.X + (box.Width - layout.WidthIncludingTrailingWhitespace) / 2,
            };

            layout.Draw(context, new Point(x, y));
        }

        /// <summary>A rounded band segment with an optional centred label.</summary>
        public static void Segment(DrawingContext context, Rect box, IBrush? fill, IBrush? stroke, string? label,
            double labelSize, IBrush labelBrush, Typeface? typeface = null, double radius = 4)
        {
            var inset = box.Deflate(new Thickness(1.5, 0));

            if (inset.Width <= 0)
            {
                return;
            }

            context.DrawRectangle(fill, stroke is null ? null : new Pen(stroke, 1.5), inset, radius, radius);

            if (!string.IsNullOrEmpty(label))
            {
                TextIn(context, label, inset, labelSize, labelBrush, typeface);
            }
        }

        public static IPen Pen(IBrush brush, double thickness = 1.5, bool dashed = false)
        {
            return new Pen(brush, thickness, dashed ? new DashStyle(new double[] { 3, 3 }, 0) : null,
                PenLineCap.Flat);
        }

        /// <summary>A soft vertical connector from a box above to a box below.</summary>
        public static void Connector(DrawingContext context, IPen pen, Point from, Point to)
        {
            var geometry = new StreamGeometry();

            using (var g = geometry.Open())
            {
                var mid = (from.Y + to.Y) / 2;
                g.BeginFigure(from, false);
                g.CubicBezierTo(new Point(from.X, mid), new Point(to.X, mid), to);
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        /// <summary>A card with a title line and key/value rows, kept inside <paramref name="bounds"/>.</summary>
        public static Rect Card(DrawingContext context, Palette palette, Point anchor, Rect bounds, string title,
            (string Key, string Value)[] rows, double size)
        {
            var keyWidth = 0.0;
            var valueWidth = 0.0;

            foreach (var (key, value) in rows)
            {
                using var k = Layout(key, size, palette.Muted);
                using var v = Layout(value, size, palette.Foreground, UiBold);
                keyWidth = Math.Max(keyWidth, k.WidthIncludingTrailingWhitespace);
                valueWidth = Math.Max(valueWidth, v.WidthIncludingTrailingWhitespace);
            }

            using var t = Layout(title, size * 1.1, palette.Foreground, UiBold);
            var pad = size * 0.8;
            var rowHeight = size * 1.45;
            var width = Math.Max(t.WidthIncludingTrailingWhitespace, keyWidth + valueWidth + size) + 2 * pad;
            var height = t.Height + rows.Length * rowHeight + 2 * pad + size * 0.3;

            var x = Math.Clamp(anchor.X - width / 2, bounds.X, Math.Max(bounds.X, bounds.Right - width));
            var y = anchor.Y;

            if (y + height > bounds.Bottom)
            {
                y = Math.Max(bounds.Y, bounds.Bottom - height);
            }

            var card = new Rect(x, y, width, height);
            context.DrawRectangle(palette.Surface, Pen(palette.Accent, 2), card, 8, 8);
            t.Draw(context, new Point(x + pad, y + pad));

            var rowY = y + pad + t.Height + size * 0.3;

            foreach (var (key, value) in rows)
            {
                Text(context, key, x + pad, rowY, size, palette.Muted);
                Text(context, value, x + pad + keyWidth + size, rowY, size, palette.Foreground, typeface: UiBold);
                rowY += rowHeight;
            }

            return card;
        }

        /// <summary>A code point as U+XXXX.</summary>
        public static string Hex(uint value) => value <= 0xFFFF ? $"U+{value:X4}" : $"U+{value:X5}";
    }
}
