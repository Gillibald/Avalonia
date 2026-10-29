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
    /// The size waterfall as a matrix: sizes down, hinting modes across, drawn through the
    /// real pipeline on an RGB-striped raster surface — the view that catches zone-policy
    /// regressions at a glance. Grayscale and true LCD output via the rendering combo.
    /// </summary>
    public partial class WaterfallView : UserControl
    {
        private TextBox _sampleBox = null!;
        private ComboBox _renderingBox = null!;
        private ComboBox _zoomBox = null!;
        private TextBlock _fontText = null!;
        private Image _matrixImage = null!;
        private Panel _matrixHost = null!;
        private int _fittedZoom;
        private GlyphTypeface? _typeface;
        private bool _initialized;

        public WaterfallView()
        {
            AvaloniaXamlLoader.Load(this);

            _sampleBox = this.FindControl<TextBox>("SampleBox")!;
            _renderingBox = this.FindControl<ComboBox>("RenderingBox")!;
            _zoomBox = this.FindControl<ComboBox>("ZoomBox")!;
            _fontText = this.FindControl<TextBlock>("FontText")!;
            _matrixImage = this.FindControl<Image>("MatrixImage")!;
            _matrixHost = this.FindControl<Panel>("MatrixHost")!;

            _renderingBox.ItemsSource = RenderingChoice.Output();
            _renderingBox.SelectedIndex = 0;
            // Zoom counts device pixels per rendered pixel, so every step is pixel exact; Fit picks
            // the largest step that fits the page.
            _zoomBox.ItemsSource = new[] { "Fit", "1x", "2x", "3x" };
            _zoomBox.SelectedIndex = 0;

            _sampleBox.TextChanged += (_, _) => Rebuild();
            _renderingBox.SelectionChanged += (_, _) => Rebuild();
            _zoomBox.SelectionChanged += (_, _) => Rebuild();
            _matrixHost.SizeChanged += (_, _) =>
            {
                if (_zoomBox.SelectedIndex == 0 && _matrixImage.Source is not null && FitZoom(_lastMatrixWidth) != _fittedZoom)
                {
                    Rebuild();
                }
            };

            _initialized = true;
        }

        public void SetTypeface(GlyphTypeface? typeface)
        {
            _typeface = typeface;
            _fontText.Text = typeface?.FamilyName;
            Rebuild();
        }

        private int _lastMatrixWidth;

        /// <summary>The largest zoom, 1 to 3 device pixels per pixel, at which the matrix fits the page.</summary>
        private int FitZoom(int matrixWidth)
        {
            var available = _matrixHost.Bounds.Width * PipelineFigures.ScaleOf(this);

            return matrixWidth <= 0 || available < 1 ? 1 : Math.Clamp((int)(available / matrixWidth), 1, 3);
        }

        private void Rebuild()
        {
            if (!_initialized || _typeface is not { } typeface ||
                _sampleBox.Text is not { Length: > 0 } sample)
            {
                return;
            }

            var rendering = _renderingBox.SelectedItem is RenderingChoice choice
                ? choice.Mode
                : TextRenderingMode.SubpixelAntialias;
            using var matrix = PipelineFigures.WaterfallMatrix(typeface, sample, rendering);

            _lastMatrixWidth = matrix.Width;

            var zoom = _zoomBox.SelectedIndex == 0 ? FitZoom(matrix.Width) : _zoomBox.SelectedIndex;

            _fittedZoom = zoom;

            SKBitmap display;

            if (zoom == 1)
            {
                display = matrix.Copy();
            }
            else
            {
                display = new SKBitmap(new SKImageInfo(matrix.Width * zoom, matrix.Height * zoom,
                    SKColorType.Bgra8888, SKAlphaType.Premul));

                using var canvas = new SKCanvas(display);
                using var image = SKImage.FromBitmap(matrix);

                canvas.DrawImage(image, new SKRect(0, 0, display.Width, display.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }

            using (display)
            {
                PipelineFigures.Show(_matrixImage, display, PipelineFigures.ScaleOf(this));
            }
        }
    }
}
