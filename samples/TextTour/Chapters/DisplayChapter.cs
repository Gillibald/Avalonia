using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace TextTour.Chapters
{
    /// <summary>
    /// A test page for an unknown display: the window's size in points and pixels, the render
    /// scaling, the screens, a text size ladder and contrast swatches to judge from the back row.
    /// </summary>
    internal sealed class DisplayChapter : Chapter
    {
        private TopLevel? _topLevel;

        public DisplayChapter(TourState state) : base(state)
        {
        }

        public override string Title => "Display";

        public override int StepCount => 1;

        public override string Caption => "TopLevel.ClientSize, TopLevel.RenderScaling, TopLevel.Screens";

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);

            if (_topLevel is not null)
            {
                _topLevel.ScalingChanged += OnTopLevelChanged;
                _topLevel.SizeChanged += OnTopLevelChanged;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_topLevel is not null)
            {
                _topLevel.ScalingChanged -= OnTopLevelChanged;
                _topLevel.SizeChanged -= OnTopLevelChanged;
            }

            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTopLevelChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var p = Palette;
            var size = Bounds.Size;

            // Rulers every 100 points along the edges show how much content area the display gives.
            var tick = Draw.Pen(p.Grid, 1);

            for (var x = 0.0; x < size.Width; x += 50)
            {
                var major = x % 100 == 0;
                context.DrawLine(major ? Draw.Pen(p.Muted, 1) : tick, new Point(x, 0), new Point(x, major ? 16 : 8));
            }

            for (var y = 0.0; y < size.Height; y += 50)
            {
                var major = y % 100 == 0;
                context.DrawLine(major ? Draw.Pen(p.Muted, 1) : tick, new Point(0, y), new Point(major ? 16 : 8, y));
            }

            context.DrawRectangle(null, Draw.Pen(p.Accent, 2), new Rect(size).Deflate(1));

            var rows = new List<(string, string)>();
            var scaling = _topLevel?.RenderScaling ?? 1;
            var client = _topLevel?.ClientSize ?? size;

            var shell = this.FindAncestorOfType<TourView>()?.Bounds.Size ?? client;

            rows.Add(("Window client size", $"{client.Width:0} x {client.Height:0} points"));
            rows.Add(("Shell size", $"{shell.Width:0} x {shell.Height:0} points"));
            rows.Add(("Render scaling", $"{scaling:0.##} ({client.Width * scaling:0} x {client.Height * scaling:0} pixels)"));
            rows.Add(("Content scale", $"{State.ContentScale * 100:0} %"));
            rows.Add(("Chapter area", $"{size.Width:0} x {size.Height:0} at content scale"));
            rows.Add(("Aspect ratio", AspectName(shell)));

            if (_topLevel?.Screens is { } screens)
            {
                var current = screens.ScreenFromTopLevel(_topLevel);

                foreach (var screen in screens.All)
                {
                    var name = screen.DisplayName ?? "Screen";
                    var mark = current is not null && screen.Equals(current) ? " (this window)" : string.Empty;
                    rows.Add((name + mark,
                        $"{screen.Bounds.Width} x {screen.Bounds.Height} px, scale {screen.Scaling:0.##}" +
                        (screen.IsPrimary ? ", primary" : string.Empty)));
                }
            }
            else
            {
                rows.Add(("Screens", "not available (offscreen rendering)"));
            }

            rows.Add(("Platform", RuntimeInformation.OSDescription));
            rows.Add(("Default font family", FontManager.Current.DefaultFontFamily.Name));
            rows.Add(("Culture", CultureInfo.CurrentCulture.Name));

            var left = Pad + 24;
            var ladderX = Math.Max(left + 520, size.Width - Pad - 420);
            var valueWidth = Math.Max(120, ladderX - left - 280 - 24);
            var top = Pad + 24;
            Draw.Text(context, "Display test", left, top, 40, p.Foreground, typeface: Draw.UiBold);
            top += 64;

            foreach (var (key, value) in rows)
            {
                Draw.Text(context, key, left, top, 22, p.Muted, maxWidth: 270);
                Draw.Text(context, value, left + 280, top, 22, p.Foreground, typeface: Draw.UiBold,
                    maxWidth: valueWidth);
                top += 34;
            }

            // Text ladder: the sizes the chapters use, to check legibility from the back of the room.
            var ladderY = Pad + 32;

            foreach (var s in new[] { 14.0, 18, 22, 28, 36, 48, 56 })
            {
                var drawn = Draw.Text(context, $"{s:0} px  Office ffi", ladderX, ladderY, s, p.Foreground);
                ladderY += drawn.Height + 4;
            }

            ladderY += 16;
            var swatch = 56.0;

            for (var i = 0; i < 8; i++)
            {
                var r = new Rect(ladderX + (i % 4) * (swatch + 8), ladderY + (i / 4) * (swatch + 8), swatch, swatch);
                context.DrawRectangle(p.SeriesFill(i), Draw.Pen(p.Series(i), 3), r, 6, 6);
                Draw.TextIn(context, $"{i + 1}", r, 22, p.Foreground, Draw.UiBold);
            }
        }

        private static string AspectName(Size size)
        {
            if (size.Height <= 0)
            {
                return "-";
            }

            var ratio = size.Width / size.Height;
            var name = Math.Abs(ratio - 16.0 / 9) < 0.03 ? "16:9" :
                Math.Abs(ratio - 16.0 / 10) < 0.03 ? "16:10" :
                Math.Abs(ratio - 4.0 / 3) < 0.03 ? "4:3" : "other";

            return $"{ratio:0.###} ({name})";
        }
    }
}
