using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace TextLab
{
    /// <summary>
    /// LCD fringe analyzer: renders the sample subpixel through the real pipeline on an
    /// RGB-striped surface, magnifies it over a per-pixel polarity classification and counts
    /// warm-left / cool-right / wrong-polarity fringes. Wrong polarity (magenta) is the
    /// defect signal for swapped stripe order or double blending; a healthy rendering shows
    /// red on left stem edges and blue on right ones. Uses the app-global font and size.
    /// </summary>
    public partial class FringesView : UserControl
    {
        private TextBox _sampleBox = null!;
        private ComboBox _hintingBox = null!;
        private ComboBox _zoomBox = null!;
        private TextBlock _fontText = null!;
        private TextBlock _statsText = null!;
        private Image _resultImage = null!;
        private GlyphTypeface? _typeface;
        private double _size = 13;
        private bool _initialized;
        private int _fittedZoom;

        public FringesView()
        {
            AvaloniaXamlLoader.Load(this);

            _sampleBox = this.FindControl<TextBox>("SampleBox")!;
            _hintingBox = this.FindControl<ComboBox>("HintingBox")!;
            _zoomBox = this.FindControl<ComboBox>("ZoomBox")!;
            _fontText = this.FindControl<TextBlock>("FontText")!;
            _statsText = this.FindControl<TextBlock>("StatsText")!;
            _resultImage = this.FindControl<Image>("ResultImage")!;

            _hintingBox.ItemsSource = new[] { TextHintingMode.Light, TextHintingMode.None, TextHintingMode.Strong };
            _hintingBox.SelectedIndex = 0;
            // Fit picks the largest whole zoom whose figure fits the page, so it is drawn near 1:1.
            _zoomBox.ItemsSource = new[] { "Fit", "4x", "6x", "8x" };
            _zoomBox.SelectedIndex = 0;

            _sampleBox.TextChanged += (_, _) => Rebuild();
            _hintingBox.SelectionChanged += (_, _) => Rebuild();
            _zoomBox.SelectionChanged += (_, _) => Rebuild();
            ((Control)_resultImage.Parent!).SizeChanged += (_, _) =>
            {
                if (_zoomBox.SelectedIndex == 0 && FitZoom() != _fittedZoom)
                {
                    Rebuild();
                }
            };
            this.FindControl<Button>("CopyButton")!.Click += (_, _) => ClipboardHelper.Copy(this,
                $"LCD fringe analysis: \"{_sampleBox.Text}\" - {_fontText.Text}, hinting {_hintingBox.SelectedItem}"
                + Environment.NewLine + _statsText.Text);

            _initialized = true;
        }

        public void SetContext(GlyphTypeface? typeface, double size)
        {
            _typeface = typeface;
            _size = size;
            _fontText.Text = typeface is null
                ? null
                : FormattableString.Invariant($"{typeface.FamilyName}, {Math.Clamp(size, 6, 96):0.#} px");
            Rebuild();
        }

        /// <summary>
        /// The largest whole zoom, 1 to 8, at which the figure fits the space the image has, from
        /// the figure's size at zoom 1.
        /// </summary>
        private int FitZoom()
        {
            if (_typeface is not { } typeface || _sampleBox.Text is not { Length: > 0 } sample)
            {
                return 1;
            }

            // The panel around the image, less the stats line above it.
            var panel = ((Control)_resultImage.Parent!).Bounds.Size;
            var available = new Avalonia.Size(panel.Width, panel.Height - _statsText.Bounds.Height - 8);
            var hinting = _hintingBox.SelectedItem is TextHintingMode mode ? mode : TextHintingMode.Light;

            using var unit = PipelineFigures.FringeAnalysis(typeface, sample, (float)Math.Clamp(_size, 6, 96), hinting, 1,
                out _, out _, out _, out _, out _);

            if (available.Width < 1 || available.Height < 1)
            {
                return 4;
            }

            var zoom = (int)Math.Min(available.Width / unit.Width, available.Height / unit.Height);

            return Math.Clamp(zoom, 1, 8);
        }

        private void Rebuild()
        {
            if (!_initialized || _typeface is not { } typeface ||
                _sampleBox.Text is not { Length: > 0 } sample)
            {
                return;
            }

            var hinting = _hintingBox.SelectedItem is TextHintingMode mode ? mode : TextHintingMode.Light;
            var size = (float)Math.Clamp(_size, 6, 96);
            var zoom = _zoomBox.SelectedIndex switch { 0 => FitZoom(), 1 => 4, 2 => 6, _ => 8 };

            _fittedZoom = zoom;

            var figure = PipelineFigures.FringeAnalysis(typeface, sample, size, hinting, zoom,
                out var fringed, out var warmEdges, out var coolEdges, out var wrongPolarity, out var inkPixels);

            var percent = inkPixels == 0 ? 0 : 100.0 * fringed / inkPixels;

            var verdict = wrongPolarity == 0 ? "  —  healthy" : "  —  CHECK STRIPE ORDER / BLENDING";

            // Healthy polarity sides mirror when the theme renders light ink on dark.
            var dark = FigureTheme.Current.IsDark;

            _statsText.Text = FormattableString.Invariant(
                $"{fringed} fringed pixels ({percent:0.0}% of {inkPixels} inked)  |  warm-{(dark ? "right" : "left")} {warmEdges}  |  cool-{(dark ? "left" : "right")} {coolEdges}  |  wrong polarity {wrongPolarity}{verdict}");

            var previous = _resultImage.Source as IDisposable;

            using (figure)
            using (var image = SKImage.FromBitmap(figure))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                _resultImage.Source = new Bitmap(stream);
            }

            previous?.Dispose();
        }
    }
}
