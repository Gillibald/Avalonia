using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace TextLab
{
    /// <summary>
    /// The pipeline walkthrough: the story of one glyph from outline to screen, told in
    /// five stages with the same live figures the inspector shows - outline and scaling,
    /// hinting, mask coverage and caching, subpixel rendering with gamma, and the tier
    /// dispatch. Figures render from the repo's Inter asset so the story is deterministic;
    /// the current font stands in when the asset is missing.
    /// </summary>
    public partial class LearnPipelineView : UserControl
    {
        private StackPanel _host = null!;
        private GlyphTypeface? _fallback;
        private bool _built;

        /// <summary>Raised by "Open the glyph inspector"; the host navigates there.</summary>
        public event Action? InspectRequested;

        public LearnPipelineView()
        {
            AvaloniaXamlLoader.Load(this);

            _host = this.FindControl<StackPanel>("Host")!;
        }

        /// <summary>The app-global typeface, used only when the repo Inter asset is absent.</summary>
        public void SetContext(GlyphTypeface? typeface) => _fallback = typeface;

        /// <summary>Rebuilds the figures - the host calls this on theme changes.</summary>
        public void Repaint()
        {
            if (_built)
            {
                _built = false;
                Build();
            }
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Build();
        }

        private void Build()
        {
            if (_built || (PipelineFigures.LoadRepoInter() ?? _fallback) is not { } typeface ||
                !typeface.CharacterToGlyphMap.ContainsGlyph('g'))
            {
                return;
            }

            _built = true;
            _host.Children.Clear();

            var g = typeface.CharacterToGlyphMap['g'];
            var e = typeface.CharacterToGlyphMap.ContainsGlyph('e') ? typeface.CharacterToGlyphMap['e'] : g;

            _host.Children.Add(new TextBlock
            {
                Text = "How a glyph reaches the screen",
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
            });
            _host.Children.Add(new TextBlock
            {
                Text = $"Five stages, one 'g', rendered live by the same code the app uses everywhere. " +
                       $"Figures use {typeface.FamilyName} at documentation sizes; the full write-up lives in docs/glyph-rasterization.",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            AddStage("1. Outlines arrive in em space",
                "A glyph starts as bezier contours in font units, drawn in an em box typically 1000 or 2048 " +
                "units tall. Scaling to a pixel size turns clean design coordinates into awkward fractions - " +
                "a stem can land at 1.37 px, which no pixel grid can draw faithfully. Below: the scaled " +
                "outline (red) over the pixels it would cover with no correction at all.",
                PipelineFigures.HintingAnatomy(typeface, g, "'g'", 12, TextHintingMode.None, out var legend1,
                    embedCaption: false), legend1);

            SKBitmap hintFigure;
            string legend2;

            if (TrueTypeHintingProbe.TryCreate(typeface, g, 12,
                    Avalonia.Media.Fonts.Rasterization.GlyphMaskMode.Antialiased, stemSnap: false,
                    out _) is { } probe)
            {
                hintFigure = PipelineFigures.BytecodeHintingAnatomy(typeface, g, "'g'", 12,
                    TextHintingMode.Light, probe, probe.StepCount, out legend2, embedCaption: false);
            }
            else
            {
                hintFigure = PipelineFigures.HintingAnatomy(typeface, g, "'g'", 12, TextHintingMode.Light,
                    out legend2, embedCaption: false);
            }

            AddStage("2. Hinting snaps the outline to the grid",
                "Hinting nudges outline points onto pixel boundaries before rasterization. Fonts that ship " +
                "TrueType programs hint themselves - the interpreter executes their bytecode, and variable " +
                "instances read a CVT adjusted by the font's cvar deltas. Everything else goes through the " +
                "geometric auto-hinter, which snaps baselines, x-height zones and stem pairs. The inspector " +
                "lets you scrub this stage instruction by instruction.",
                hintFigure, legend2);

            AddStage("3. Coverage becomes a cached mask",
                "The analytic rasterizer integrates exact area coverage per pixel - no supersampling - and " +
                "stores the result as an 8-bit mask with a one-pixel apron, keyed by glyph, quantized size, " +
                "subpixel phase and hinting flags. Whole runs then compose from cached masks at integer pen " +
                "positions, which is why warm frames draw with zero allocations.",
                PipelineFigures.MaskAnatomy(typeface, g, "'g'", "Hamburg", 13, out var legend3,
                    embedInfo: false), legend3);

            AddStage("4. Subpixel rendering triples the horizontal grid",
                "For LCD output the rasterizer runs at three times the horizontal resolution, filters each " +
                "stripe with a five-tap FIR to tame color fringing, and interleaves the result into an RGB " +
                "mask. Gamma correction linearizes the per-channel blend so dark-on-light and light-on-dark " +
                "text carry the same apparent weight - flip the app theme and the Fringes view to see both " +
                "polarities.",
                PipelineFigures.ClearTypePipeline(typeface, e, "'e'", 13, bgr: false, gamma: true,
                    TextHintingMode.Light, out var legend4, embedCaption: false), legend4);

            AddStage("5. Three tiers draw the run",
                "Every glyph run dispatches through up to three tiers: pre-composed run masks for " +
                "axis-aligned text, the Slug vector tier on the GPU for rotated and very large text - its " +
                "band-partitioned quadratic payload is shown below - and the backend's native blob as the " +
                "final fallback. A declined draw falls through to the next tier, so text never silently " +
                "fails to render.",
                PipelineFigures.SlugBands(typeface, g, "'g'", out var legend5, embedCaption: false), legend5);

            var inspectButton = new Button
            {
                Content = "Open the glyph inspector",
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 12),
            };

            inspectButton.Click += (_, _) => InspectRequested?.Invoke();

            _host.Children.Add(new TextBlock
            {
                Text = "The interactive versions of these figures live in the glyph inspector - pick any " +
                       "glyph in the Glyphs view and scrub its bytecode.",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            _host.Children.Add(inspectButton);
        }

        private void AddStage(string title, string prose, SKBitmap figure, string legend)
        {
            _host.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
            });
            _host.Children.Add(new TextBlock
            {
                Text = prose,
                FontSize = 13,
                MaxWidth = 760,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });

            var image = new Image { Stretch = Stretch.None, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };

            SetImage(image, figure);
            _host.Children.Add(image);

            if (legend.Length > 0)
            {
                _host.Children.Add(new TextBlock
                {
                    Text = legend,
                    FontSize = 11,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        private static void SetImage(Image target, SKBitmap figure)
        {
            using (figure)
            using (var image = SKImage.FromBitmap(figure))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                target.Source = new Bitmap(stream);
            }
        }
    }
}
