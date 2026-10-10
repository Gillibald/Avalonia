using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TextShowcase.Diagnostics;

namespace TextShowcase.Scenes
{
    /// <summary>
    /// Bundled fonts only, at fixed sizes in a fixed-size pane, so a capture of this scene on
    /// Windows, macOS, Linux and Android differs only by what the pipelines do. The Managed side
    /// is expected to match across machines; the Backend side shows each platform's own scaler.
    /// </summary>
    internal sealed class ReferenceScene : Scene
    {
        public override string Title => "Same pixels everywhere";

        public override string Caption =>
            "Only fonts that ship with the app. Press C on each platform: Managed captures should match byte for byte, Backend captures follow DirectWrite, CoreText or FreeType.";

        public override string? NoteFor(TextRasterizationMode mode) => mode == TextRasterizationMode.Backend
            ? "Backend on " + PlatformInfo.OsName + " uses " + PlatformInfo.BackendScaler
            : "Platform default here: " + PlatformInfo.PlatformDefault;

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(2);

            root.Children.Add(Ui.Label("Inter 13 / 16 / 24 px"));
            root.Children.Add(Wrapped("Hamburgefonstiv 0123456789 - the quick brown fox jumps over the lazy dog. Stems, bowls and spacing at UI sizes.", 13));
            root.Children.Add(Wrapped("Hamburgefonstiv 0123456789 - the quick brown fox jumps over the lazy dog.", 16));
            root.Children.Add(Ui.Text("Hamburgefonstiv 0123", 24, Ui.Inter));

            root.Children.Add(Ui.Label("Inter Variable, wght 650 / wght 300 (a varied instance)"));
            var heavy = Ui.Text("Variable instances render alike", 22, Ui.InterVariable);
            heavy.FontVariations = FontVariationSettings.Parse("wght=650");
            root.Children.Add(heavy);
            var light = Ui.Text("Variable instances render alike", 22, Ui.InterVariable);
            light.FontVariations = FontVariationSettings.Parse("wght=300");
            root.Children.Add(light);

            root.Children.Add(Ui.Label("Noto Sans Arabic 22 px"));
            var arabic = Ui.Text("أبجد هوز حطي كلمن سعفص قرشت ١٢٣", 22, Ui.NotoSansArabic);
            arabic.FlowDirection = FlowDirection.RightToLeft;
            arabic.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            root.Children.Add(arabic);

            root.Children.Add(Ui.Label("Noto Sans Hebrew 20 px"));
            var hebrew = Ui.Text("אבגד הוזח טיכל מנסע פצקר שת", 20, Ui.NotoSansHebrew);
            hebrew.FlowDirection = FlowDirection.RightToLeft;
            hebrew.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            root.Children.Add(hebrew);

            root.Children.Add(Ui.Label("WenQuanYi Micro Hei 20 px"));
            root.Children.Add(Ui.Text("永字八法 文本渲染在每个平台上都一样", 20, Ui.WenQuanYi));

            root.Children.Add(Ui.Label("Noto Mono: regular, synthetic bold, synthetic oblique"));
            root.Children.Add(Ui.Row(24,
                Ui.Text("Regular {}", 18, Ui.NotoMono),
                Ui.Text("Bold {}", 18, Ui.NotoMono, FontWeight.Bold),
                Ui.Text("Oblique {}", 18, Ui.NotoMono, FontWeight.Normal, FontStyle.Italic)));

            return root;
        }

        private static Control Wrapped(string text, double size)
        {
            var block = Ui.Text(text, size, Ui.Inter);
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, 0, 0, 4);
            return block;
        }
    }
}
