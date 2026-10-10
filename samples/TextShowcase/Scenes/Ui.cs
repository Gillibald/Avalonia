using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TextShowcase.Panes;

namespace TextShowcase.Scenes
{
    /// <summary>Font families the scenes use and small builders for pane content.</summary>
    internal static class Ui
    {
        private const string Bundled = "avares://TextShowcase/Assets/Fonts#";

        // Repository fonts: identical files on every platform.
        public static readonly FontFamily Inter = new("fonts:Inter#Inter");
        public static readonly FontFamily InterVariable = new(Bundled + "Inter Variable");
        public static readonly FontFamily NotoSansArabic = new(Bundled + "Noto Sans Arabic");
        public static readonly FontFamily NotoSansHebrew = new(Bundled + "Noto Sans Hebrew");
        public static readonly FontFamily NotoMono = new(Bundled + "Noto Mono");
        public static readonly FontFamily WenQuanYi = new(Bundled + "WenQuanYi Micro Hei");

        // System fonts: present on Windows, resolved through fallback elsewhere.
        public static readonly FontFamily SegoeUI = new("Segoe UI");
        public static readonly FontFamily SegoeUIVariable = new("Segoe UI Variable");
        public static readonly FontFamily Bahnschrift = new("Bahnschrift");
        public static readonly FontFamily Georgia = new("Georgia");
        public static readonly FontFamily SegoeUIEmoji = new("Segoe UI Emoji");
        public static readonly FontFamily NotoColorEmoji = new("Noto Color Emoji");
        public static readonly FontFamily YaHei = new("Microsoft YaHei");
        public static readonly FontFamily Code = new("Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, monospace");

        public static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
        public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x76));
        public static readonly IBrush Hairline = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8));
        public static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xD6));

        public static PaneText Text(string text, double size, FontFamily? family = null,
            FontWeight weight = FontWeight.Normal, FontStyle style = FontStyle.Normal, IBrush? foreground = null)
            => new()
            {
                Text = text,
                FontSize = size,
                FontFamily = family ?? Inter,
                FontWeight = weight,
                FontStyle = style,
                Foreground = foreground ?? Ink,
            };

        /// <summary>A small grey row label in the pane's own mode, so the label is part of the comparison.</summary>
        public static PaneText Label(string text) => new()
        {
            Text = text,
            FontSize = 14,
            FontFamily = Inter,
            Foreground = Muted,
            Margin = new Thickness(0, 10, 0, 2),
        };

        public static StackPanel Column(double spacing = 6, params Control[] children)
        {
            var panel = new StackPanel { Spacing = spacing };

            foreach (var child in children)
            {
                panel.Children.Add(child);
            }

            return panel;
        }

        public static StackPanel Row(double spacing, params Control[] children)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = spacing,
            };

            foreach (var child in children)
            {
                panel.Children.Add(child);
            }

            return panel;
        }

        public static Border Rule() => new()
        {
            Height = 1,
            Background = Hairline,
            Margin = new Thickness(0, 8),
        };
    }
}
