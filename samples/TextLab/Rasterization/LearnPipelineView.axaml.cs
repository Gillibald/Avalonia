using System;
using System.Collections.Generic;
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
    /// the current font stands in when the asset is missing. One stage shows at a time, stepped
    /// with Previous and Next, so every stage fits the window without scrolling.
    /// </summary>
    public partial class LearnPipelineView : UserControl
    {
        /// <summary>One page of the walkthrough.</summary>
        private sealed record Stage(string Title, string Prose, Bitmap? Figure, Size FigureSize, string Legend);

        private readonly List<Stage> _stages = new();
        private readonly TextBlock _introText;
        private readonly TextBlock _pageText;
        private readonly TextBlock _stageTitle;
        private readonly TextBlock _stageProse;
        private readonly TextBlock _stageLegend;
        private readonly Image _stageFigure;
        private readonly Button _previousButton;
        private readonly Button _nextButton;
        private readonly Button _inspectButton;
        private GlyphTypeface? _fallback;
        private bool _built;
        private int _page;

        /// <summary>Raised by "Open the glyph inspector"; the host navigates there.</summary>
        public event Action? InspectRequested;

        public LearnPipelineView()
        {
            AvaloniaXamlLoader.Load(this);

            _introText = this.FindControl<TextBlock>("IntroText")!;
            _pageText = this.FindControl<TextBlock>("PageText")!;
            _stageTitle = this.FindControl<TextBlock>("StageTitle")!;
            _stageProse = this.FindControl<TextBlock>("StageProse")!;
            _stageLegend = this.FindControl<TextBlock>("StageLegend")!;
            _stageFigure = this.FindControl<Image>("StageFigure")!;
            _previousButton = this.FindControl<Button>("PreviousButton")!;
            _nextButton = this.FindControl<Button>("NextButton")!;
            _inspectButton = this.FindControl<Button>("InspectButton")!;

            _previousButton.Click += (_, _) => ShowPage(_page - 1);
            _nextButton.Click += (_, _) => ShowPage(_page + 1);
            _inspectButton.Click += (_, _) => InspectRequested?.Invoke();
        }

        /// <summary>The number of stages, once the figures are built.</summary>
        public int PageCount
        {
            get
            {
                Build();
                return Math.Max(1, _stages.Count);
            }
        }

        /// <summary>Shows stage <paramref name="index"/>, clamped to the stages there are.</summary>
        public void ShowPage(int index)
        {
            Build();

            if (_stages.Count == 0)
            {
                return;
            }

            _page = Math.Clamp(index, 0, _stages.Count - 1);

            var stage = _stages[_page];

            _stageTitle.Text = stage.Title;
            _stageProse.Text = stage.Prose;
            _stageLegend.Text = stage.Legend;
            _stageLegend.IsVisible = stage.Legend.Length > 0;
            _stageFigure.Source = stage.Figure;
            _stageFigure.MaxWidth = stage.FigureSize.Width;
            _stageFigure.MaxHeight = stage.FigureSize.Height;
            _inspectButton.IsVisible = _page == _stages.Count - 1;
            _pageText.Text = $"Stage {_page + 1} of {_stages.Count}";
            _previousButton.IsEnabled = _page > 0;
            _nextButton.IsEnabled = _page < _stages.Count - 1;
        }

        /// <summary>Steps <paramref name="delta"/> stages; the host forwards paging keys here.</summary>
        public void Step(int delta) => ShowPage(_page + delta);

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
            _stages.Clear();

            var g = typeface.CharacterToGlyphMap['g'];
            var e = typeface.CharacterToGlyphMap.ContainsGlyph('e') ? typeface.CharacterToGlyphMap['e'] : g;

            _introText.Text = $"Five stages, one 'g', rendered live by the same code the app uses everywhere. " +
                              $"Figures use {typeface.FamilyName} at documentation sizes; the full write-up lives in docs/glyph-rasterization.";

            AddStage("1. Outlines arrive in em space",
                "A glyph starts as bezier contours in font units, drawn in an em box typically 1000 or 2048 " +
                "units tall. Scaling to a pixel size turns clean design coordinates into awkward fractions - " +
                "a stem can land at 1.37 px, which no pixel grid can draw faithfully. Beside: the scaled " +
                "outline (red) over the pixels it would cover with no correction at all.",
                PipelineFigures.HintingAnatomy(typeface, g, "'g'", 12, TextHintingMode.None, out var legend1,
                    embedCaption: false, deviceScale: PipelineFigures.ScaleOf(this)), legend1);

            SKBitmap hintFigure;
            string legend2;

            if (TrueTypeHintingProbe.TryCreate(typeface, g, 12,
                    Avalonia.Media.Fonts.Rasterization.GlyphMaskMode.Antialiased, strong: false,
                    out _) is { } probe)
            {
                hintFigure = PipelineFigures.BytecodeHintingAnatomy(typeface, g, "'g'", 12,
                    TextHintingMode.Light, probe, probe.StepCount, out legend2, embedCaption: false, deviceScale: PipelineFigures.ScaleOf(this));
            }
            else
            {
                hintFigure = PipelineFigures.HintingAnatomy(typeface, g, "'g'", 12, TextHintingMode.Light,
                    out legend2, embedCaption: false, deviceScale: PipelineFigures.ScaleOf(this));
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
                    embedInfo: false, deviceScale: PipelineFigures.ScaleOf(this)), legend3);

            AddStage("4. Subpixel rendering triples the horizontal grid",
                "For LCD output the rasterizer runs at three times the horizontal resolution, filters each " +
                "stripe with a five-tap FIR to tame color fringing, and interleaves the result into an RGB " +
                "mask. Gamma correction linearizes the per-channel blend so dark-on-light and light-on-dark " +
                "text carry the same apparent weight - flip the app theme and the Fringes view to see both " +
                "polarities.",
                PipelineFigures.ClearTypePipeline(typeface, e, "'e'", 13, bgr: false, gamma: true,
                    TextHintingMode.Light, out var legend4, embedCaption: false, deviceScale: PipelineFigures.ScaleOf(this)), legend4);

            AddStage("5. Three tiers draw the run",
                "Every glyph run dispatches through up to three tiers: pre-composed run masks for " +
                "axis-aligned text, transformed glyph masks for rotated, skewed and very large text - " +
                "rasterized under the device transform, and again every frame while that transform " +
                "animates - and the backend's native blob as the final fallback. A declined draw falls " +
                "through to the next tier, so text never silently fails to render.",
                null, "The interactive versions of these figures live in the glyph inspector - pick any glyph " +
                      "in the Glyphs view and scrub its bytecode.");

            ShowPage(_page);
        }

        private void AddStage(string title, string prose, SKBitmap? figure, string legend)
        {
            using (figure)
            {
                // Figures are drawn at device scale and shown at their size in points.
                var deviceScale = PipelineFigures.ScaleOf(this);

                _stages.Add(new Stage(title, prose, figure is null ? null : PipelineFigures.ToBitmap(figure),
                    figure is null ? default : new Size(figure.Width / deviceScale, figure.Height / deviceScale), legend));
            }
        }
    }
}
