using System;
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

        // System fonts: the first candidate the system has, so rows name the family that draws them.
        public static readonly SystemFont Sans = Installed("Segoe UI") ?? Default();
        public static readonly SystemFont Serif = Installed("Georgia", "Times New Roman", "DejaVu Serif") ?? Default();
        public static readonly SystemFont ArabicSans = Installed("Segoe UI", "Geeza Pro", "Noto Sans Arabic UI") ?? Default();
        public static readonly SystemFont Cjk =
            Installed("Microsoft YaHei", "PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC") ?? Default();
        public static readonly SystemFont? Emoji = Installed("Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji");
        public static readonly FontFamily Code = new("Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, monospace");

        public static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
        public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x76));
        public static readonly IBrush Hairline = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8));
        public static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xD6));

        /// <summary>
        /// The first of <paramref name="names"/> the system resolves to a face of that family, or
        /// <see langword="null"/> when it has none of them.
        /// </summary>
        public static SystemFont? Installed(params string[] names)
        {
            foreach (var name in names)
            {
                var family = new FontFamily(name);

                if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var typeface) &&
                    string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new SystemFont(family, name);
                }
            }

            return null;
        }

        /// <summary>The platform's default family, named as the font system reports it.</summary>
        private static SystemFont Default()
        {
            var family = FontFamily.Default;

            return new SystemFont(family,
                FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var typeface) ? typeface.FamilyName : family.Name);
        }

        /// <summary>
        /// The family that draws <paramref name="codepoint"/> when <paramref name="family"/> lacks it, as
        /// font fallback picks it.
        /// </summary>
        public static string FallbackName(int codepoint, FontFamily family) =>
            FontManager.Current.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                family, null, out var typeface)
                ? typeface.FontFamily.Name
                : "none";

        /// <summary>
        /// <paramref name="font"/>'s name, followed by the fallback family when the font lacks
        /// <paramref name="codepoint"/>.
        /// </summary>
        public static string NameFor(SystemFont font, int codepoint)
        {
            var used = FallbackName(codepoint, font.Family);

            return string.Equals(used, font.Name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(used, font.Family.Name, StringComparison.OrdinalIgnoreCase)
                ? font.Name
                : font.Name + ", fallback to " + used;
        }

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

    /// <summary>A system family a scene draws with, and the name its labels show.</summary>
    internal sealed record SystemFont(FontFamily Family, string Name);
}
