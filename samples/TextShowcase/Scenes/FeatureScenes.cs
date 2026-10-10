using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TextShowcase.Panes;

namespace TextShowcase.Scenes
{
    /// <summary>Variable font axes animated live; the instance changes every frame.</summary>
    internal sealed class VariableFontsScene : Scene
    {
        private static readonly (string Label, FontFamily Family, double Size, string Axes)[] s_lines =
        {
            ("Bahnschrift  wght 300-700, wdth 75-100", Ui.Bahnschrift, 46, "wght wdth"),
            ("Segoe UI Variable  wght 300-700, opsz 8-36", Ui.SegoeUIVariable, 40, "wght opsz"),
            ("Inter Variable (bundled)  wght 100-900, opsz 14-32", Ui.InterVariable, 40, "wght opsz"),
        };

        public override string Title => "Variable fonts, animated";

        public override string Caption =>
            "Every frame is a new instance: gvar outlines, HVAR advances, cvar hinting. Skia cannot vary a face it did not load, so varied runs take the managed path in both panes.";

        public override string? NoteFor(TextRasterizationMode mode) => mode == TextRasterizationMode.Backend
            ? "Varied faces draw managed here too"
            : null;

        public override bool IsAnimated => true;

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(4);

            foreach (var (label, family, size, axes) in s_lines)
            {
                root.Children.Add(Ui.Label(label));

                var sample = Ui.Text("Handgloves 0123 Variable", size, family);
                var readout = Ui.Text("", 15, Ui.NotoMono, foreground: Ui.Muted);
                var phaseOffset = root.Children.Count * 0.7;

                context.OnTick(time =>
                {
                    // One phase drives every axis, quantized, so the number of distinct instances stays small.
                    var phase = 0.5 - 0.5 * Math.Cos((time * 0.9) + phaseOffset);
                    var step = Math.Round(phase * 40) / 40;
                    var wght = axes.Contains("wght") ? Lerp(family == Ui.InterVariable ? 100 : 300,
                        family == Ui.InterVariable ? 900 : 700, step) : 400;
                    var second = axes.Contains("wdth") ? Lerp(100, 75, step)
                        : family == Ui.InterVariable ? Lerp(14, 32, step) : Lerp(8, 36, step);
                    var tag = axes.Contains("wdth") ? "wdth" : "opsz";
                    var settings = FormattableString.Invariant($"wght={wght:0}, {tag}={second:0.#}");

                    if (sample.Tag as string != settings)
                    {
                        sample.Tag = settings;
                        sample.FontVariations = FontVariationSettings.Parse(settings);
                        readout.Text = settings;
                    }
                });

                root.Children.Add(sample);
                root.Children.Add(readout);
            }

            root.Children.Add(Ui.Label("Inter Variable paragraph at a fixed instance (wght 480, opsz 18)"));
            var paragraph = Ui.Text(
                "Variable fonts carry a design space instead of a few static weights. Avalonia reads fvar, avar, gvar, HVAR, MVAR and cvar itself, so every platform gets the same instance.",
                18, Ui.InterVariable);
            paragraph.TextWrapping = TextWrapping.Wrap;
            paragraph.FontVariations = FontVariationSettings.Parse("wght=480, opsz=18");
            root.Children.Add(paragraph);

            return root;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }

    /// <summary>Colour glyphs through plain text controls, so the product's colour path draws them.</summary>
    internal sealed class ColorGlyphsScene : Scene
    {
        public override string Title => "Colour glyphs";

        public override string Caption =>
            "COLR v0 layers, COLR v1 paint graphs and bitmap strikes are drawn by Avalonia's own colour path in both modes, whatever the platform's scaler supports.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(4);

            root.Children.Add(Ui.Label("Segoe UI Emoji (COLR), 24 / 40 / 64 / 96 px"));
            var ramp = Ui.Row(18);

            foreach (var size in new[] { 24.0, 40, 64, 96 })
            {
                var text = Ui.Text("\U0001F98A\U0001F308", size, Ui.SegoeUIEmoji);
                text.VerticalAlignment = VerticalAlignment.Bottom;
                ramp.Children.Add(text);
            }

            root.Children.Add(ramp);

            root.Children.Add(Ui.Label("Sequences: ZWJ, skin tone, flag, keycap"));
            root.Children.Add(Ui.Text(
                "\U0001F469‍\U0001F4BB \U0001F468‍\U0001F469‍\U0001F467 \U0001F44B\U0001F3FD \U0001F3F3️‍\U0001F308 \U0001F1E9\U0001F1EA 1️⃣ #️⃣",
                40, Ui.SegoeUIEmoji));

            root.Children.Add(Ui.Label("Emoji inside text, Inter with fallback"));
            root.Children.Add(Ui.Text("Ship it \U0001F680 tonight, review ✅ tomorrow, celebrate \U0001F389", 28, Ui.Inter));

            if (Find("Noto Color Emoji", bitmap: false) is { } noto)
            {
                root.Children.Add(Ui.Label("Noto Color Emoji (COLRv1, gradients)"));
                root.Children.Add(Ui.Text("\U0001F98A\U0001F308\U0001F680\U0001F3A8\U0001F30D\U0001F525", 56, noto));
            }

            root.Children.Add(Ui.Label("Bitmap strike (CBDT or sbix)"));

            if (Find(new[] { "Apple Color Emoji", "Noto Color Emoji", "Noto Color Emoji Compat" }) is { } strike)
            {
                root.Children.Add(Ui.Text("\U0001F98A\U0001F308\U0001F680\U0001F3A8", 56, strike));
            }
            else
            {
                root.Children.Add(Ui.Text("No bitmap-strike emoji font on this machine (Windows ships none; macOS and Android do).", 16,
                    Ui.Inter, foreground: Ui.Muted));
            }

            return root;
        }

        private static FontFamily? Find(string family, bool bitmap)
        {
            var candidate = new FontFamily(family);

            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var typeface) &&
                string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase) &&
                (!bitmap || typeface.BitmapSource is not null))
            {
                return candidate;
            }

            return null;
        }

        private static FontFamily? Find(string[] families)
        {
            foreach (var family in families)
            {
                if (Find(family, bitmap: true) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>A waterfall under each hinting mode: the font's own TrueType programs on the managed side.</summary>
    internal sealed class HintingScene : Scene
    {
        public override string Title => "Hinting with the font's own programs";

        public override string Caption =>
            "Managed runs each font's TrueType bytecode (fpgm, prep, glyph programs, gasp) for every hinting mode. The backend renders with the platform's default hinting whatever the mode; on Windows that is always DirectWrite's.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(2);

            root.Children.Add(Waterfall(Ui.SegoeUI, "Segoe UI", new[] { 9, 10, 11, 12, 13, 14, 16, 18 }));
            root.Children.Add(Ui.Rule());
            root.Children.Add(Waterfall(Ui.Georgia, "Georgia", new[] { 10, 11, 12, 14, 16 }));

            return root;
        }

        private static Control Waterfall(FontFamily family, string name, int[] sizes)
        {
            var modes = new[] { TextHintingMode.None, TextHintingMode.Light, TextHintingMode.Strong };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("48,*,*,*"),
            };

            for (var row = 0; row <= sizes.Length; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            for (var column = 0; column < modes.Length; column++)
            {
                var header = Ui.Label(name + "  " + modes[column]);
                Grid.SetColumn(header, column + 1);
                grid.Children.Add(header);
            }

            for (var row = 0; row < sizes.Length; row++)
            {
                var size = sizes[row];
                var label = Ui.Text(size + " px", 12, Ui.Inter, foreground: Ui.Muted);
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(label, row + 1);
                grid.Children.Add(label);

                for (var column = 0; column < modes.Length; column++)
                {
                    var sample = Ui.Text("Hamburgefonstiv 0123", size, family);
                    sample.Margin = new Thickness(0, 2);
                    TextOptions.SetTextHintingMode(sample, modes[column]);
                    Grid.SetRow(sample, row + 1);
                    Grid.SetColumn(sample, column + 1);
                    grid.Children.Add(sample);
                }
            }

            return grid;
        }
    }

    /// <summary>Synthetic bold and oblique on faces that have no such style.</summary>
    internal sealed class SimulationsScene : Scene
    {
        public override string Title => "Simulations: synthetic bold and oblique";

        public override string Caption =>
            "Families with a single regular face, requested bold and italic. Managed emboldens and slants outlines only; colour glyphs are never simulated.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(4);
            var styles = new (string Name, FontWeight Weight, FontStyle Style)[]
            {
                ("Regular", FontWeight.Normal, FontStyle.Normal),
                ("Bold (synthetic)", FontWeight.Bold, FontStyle.Normal),
                ("Oblique (synthetic)", FontWeight.Normal, FontStyle.Italic),
                ("Bold oblique (synthetic)", FontWeight.Bold, FontStyle.Italic),
            };

            root.Children.Add(Ui.Label("Noto Mono (bundled, one regular face) 30 px"));

            foreach (var (name, weight, style) in styles)
            {
                root.Children.Add(Ui.Text("Sphinx of quartz {} " + name, 30, Ui.NotoMono, weight, style));
            }

            root.Children.Add(Ui.Label("WenQuanYi Micro Hei (bundled) 30 px: regular, bold, oblique"));
            root.Children.Add(Ui.Row(28,
                Ui.Text("文本渲染", 30, Ui.WenQuanYi),
                Ui.Text("文本渲染", 30, Ui.WenQuanYi, FontWeight.Bold),
                Ui.Text("文本渲染", 30, Ui.WenQuanYi, FontWeight.Normal, FontStyle.Italic)));

            root.Children.Add(Ui.Label("Noto Sans Arabic (bundled) 30 px: regular, bold"));
            var arabic = Ui.Row(28,
                Ui.Text("النص العربي", 30, Ui.NotoSansArabic),
                Ui.Text("النص العربي", 30, Ui.NotoSansArabic, FontWeight.Bold));
            root.Children.Add(arabic);

            root.Children.Add(Ui.Label("Oblique with emoji: the colour glyph stays upright"));
            root.Children.Add(Ui.Text("Oblique text \U0001F680\U0001F308 with emoji", 30,
                new FontFamily(Ui.NotoMono.Name + ", Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"),
                FontWeight.Normal, FontStyle.Italic));

            return root;
        }
    }

    /// <summary>Complex scripts and font fallback.</summary>
    internal sealed class ScriptsScene : Scene
    {
        public override string Title => "Complex scripts and fallback";

        public override string Caption =>
            "HarfBuzz shapes, Avalonia picks fallback fonts that can shape the script, and both pipelines draw the same glyphs at the same positions.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(2);

            Add(root, "Arabic, Noto Sans Arabic (bundled)", "اللغة العربية جميلة ١٢٣٤ مرحبا بالعالم", Ui.NotoSansArabic, 30, rtl: true);
            Add(root, "Arabic, Segoe UI", "اللغة العربية جميلة ١٢٣٤ مرحبا بالعالم", Ui.SegoeUI, 30, rtl: true);
            Add(root, "Hebrew, Noto Sans Hebrew (bundled)", "שלום עולם, טקסט בעברית", Ui.NotoSansHebrew, 28, rtl: true);
            Add(root, "Devanagari (fallback, Nirmala UI on Windows)", "हिन्दी देवनागरी लिपि में संयुक्ताक्षर क्ष त्र ज्ञ", Ui.Inter, 30);
            Add(root, "Thai (fallback)", "ภาษาไทย การจัดวางสระและวรรณยุกต์", Ui.Inter, 28);
            Add(root, "Chinese, Microsoft YaHei", "永字八法 中文排版 文字渲染", Ui.YaHei, 30);
            Add(root, "Japanese and Korean (fallback)", "日本語のテキスト  한국어 텍스트", Ui.Inter, 28);
            Add(root, "One line, Inter first, everything else by fallback",
                "Inter | العربية | हिन्दी | ไทย | 中文 | 한국어 | \U0001F680", Ui.Inter, 26);

            return root;
        }

        private static void Add(StackPanel root, string label, string text, FontFamily family, double size, bool rtl = false)
        {
            root.Children.Add(Ui.Label(label));

            var sample = Ui.Text(text, size, family);

            if (rtl)
            {
                sample.FlowDirection = FlowDirection.RightToLeft;
                sample.HorizontalAlignment = HorizontalAlignment.Left;
            }

            root.Children.Add(sample);
        }
    }

    /// <summary>Grayscale against subpixel (LCD) text on the window surface.</summary>
    internal sealed class LcdScene : Scene
    {
        public override string Title => "Subpixel (LCD) text";

        public override string Caption =>
            "LCD text where the surface allows it: on screen here. Offscreen and alpha surfaces fall back to grayscale, which is why captures of this scene show grayscale only.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(4);
            var modes = new[]
            {
                ("SubpixelAntialias", TextRenderingMode.SubpixelAntialias),
                ("Antialias (grayscale)", TextRenderingMode.Antialias),
                ("Alias", TextRenderingMode.Alias),
            };

            foreach (var (name, mode) in modes)
            {
                var block = Ui.Column(2);
                block.Children.Add(Ui.Label(name));
                block.Children.Add(Ui.Text("Segoe UI 13 px: The quick brown fox jumps over the lazy dog 0123456789", 13, Ui.SegoeUI));
                block.Children.Add(Ui.Text("Segoe UI 16 px: The quick brown fox jumps over the lazy dog", 16, Ui.SegoeUI));
                block.Children.Add(Ui.Text("Inter 14 px: The quick brown fox jumps over the lazy dog 0123", 14, Ui.Inter));
                block.Children.Add(Ui.Text("Georgia 15 px: The quick brown fox jumps over the lazy dog", 15, Ui.Georgia));
                TextOptions.SetTextRenderingMode(block, mode);
                block.Margin = new Thickness(0, 0, 0, 14);
                root.Children.Add(block);
            }

            return root;
        }
    }
}
