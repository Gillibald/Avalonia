using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TextShowcase.Scenes
{
    /// <summary>The opener: ordinary application text, the same tree drawn by both pipelines.</summary>
    internal sealed class SideBySideScene : Scene
    {
        public override string Title => "Same text, two pipelines";

        public override string Caption =>
            "One window, one visual tree per side. Left: Avalonia rasterizes every glyph. Right: Skia's text stack. Press D for the pixel diff.";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(4);

            root.Children.Add(Ui.Text("Typography gives language a body", 50, Ui.SegoeUI, FontWeight.SemiBold));

            var weights = Ui.Row(18);

            foreach (var (label, weight) in new[]
                     {
                         ("Light", FontWeight.Light), ("Regular", FontWeight.Normal),
                         ("Semibold", FontWeight.SemiBold), ("Bold", FontWeight.Bold), ("Black", FontWeight.Black),
                     })
            {
                weights.Children.Add(Ui.Text("Aa " + label, 30, Ui.SegoeUI, weight));
            }

            root.Children.Add(weights);

            var ramp = Ui.Row(14);

            foreach (var size in new[] { 11, 13, 16, 20, 26, 34, 46, 64 })
            {
                var text = Ui.Text("Ag", size, Ui.SegoeUI);
                text.VerticalAlignment = VerticalAlignment.Bottom;
                ramp.Children.Add(text);
            }

            ramp.Margin = new Thickness(0, 6, 0, 6);
            root.Children.Add(ramp);

            root.Children.Add(Ui.Label("Segoe UI 18 px"));
            root.Children.Add(Paragraph(Ui.SegoeUI, 18));
            root.Children.Add(Ui.Label("Inter 18 px (bundled with Avalonia)"));
            root.Children.Add(Paragraph(Ui.Inter, 18));
            root.Children.Add(Ui.Label("Small UI text, Segoe UI 12 px"));
            root.Children.Add(Ui.Text("File  Edit  View  Window  Help    Ctrl+Shift+P    42 items selected    1,234.56", 12, Ui.SegoeUI));
            root.Children.Add(Ui.Label("CJK, Microsoft YaHei 24 px"));
            root.Children.Add(Ui.Text("永字八法 跨平台的文本渲染 あいうえお 한국어 텍스트", 24, Ui.YaHei));

            return root;
        }

        private static Control Paragraph(FontFamily family, double size)
        {
            var text = Ui.Text(
                "A text engine earns its keep in the details: consistent stems at small sizes, even colour " +
                "across a paragraph, correct joining in scripts far from Latin, and pixels that survive a loupe. " +
                "The quick brown fox jumps over the lazy dog, 0123456789.",
                size, family);

            text.TextWrapping = TextWrapping.Wrap;
            text.LineHeight = size * 1.45;

            return text;
        }
    }
}
