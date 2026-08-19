using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace TextTestApp
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
            _zoomBox.ItemsSource = new[] { "4x", "6x", "8x" };
            _zoomBox.SelectedIndex = 1;

            _sampleBox.TextChanged += (_, _) => Rebuild();
            _hintingBox.SelectionChanged += (_, _) => Rebuild();
            _zoomBox.SelectionChanged += (_, _) => Rebuild();
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

        private void Rebuild()
        {
            if (!_initialized || _typeface is not { } typeface ||
                _sampleBox.Text is not { Length: > 0 } sample)
            {
                return;
            }

            var hinting = _hintingBox.SelectedItem is TextHintingMode mode ? mode : TextHintingMode.Light;
            var zoom = _zoomBox.SelectedIndex switch { 0 => 4, 1 => 6, _ => 8 };
            var size = (float)Math.Clamp(_size, 6, 96);

            var figure = PipelineFigures.FringeAnalysis(typeface, sample, size, hinting, zoom,
                out var fringed, out var warmLeft, out var coolRight, out var wrongPolarity, out var inkPixels);

            var percent = inkPixels == 0 ? 0 : 100.0 * fringed / inkPixels;

            var verdict = wrongPolarity == 0 ? "  —  healthy" : "  —  CHECK STRIPE ORDER / BLENDING";

            _statsText.Text = FormattableString.Invariant(
                $"{fringed} fringed pixels ({percent:0.0}% of {inkPixels} inked)  |  warm-left {warmLeft}  |  cool-right {coolRight}  |  wrong polarity {wrongPolarity}{verdict}");

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
