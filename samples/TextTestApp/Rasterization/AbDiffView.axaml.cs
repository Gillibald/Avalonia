using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// A/B comparison of the same text rendered under two configurations (rasterization
    /// mode, hinting, rendering mode): side by side, an aligned overlay (A red, B blue), a
    /// per-pixel difference heat map and numeric stats. B can be replaced by a saved
    /// reference PNG, which turns the view into a between-commits regression check.
    /// Rendering goes through RenderTargetBitmap, i.e. the real drawing pipeline on a CPU
    /// raster target.
    /// </summary>
    public partial class AbDiffView : UserControl
    {
        private static readonly string[] s_modes = { "Managed", "Backend" };

        private TextBox _textBox = null!;
        private TextBox _fontBox = null!;
        private NumericUpDown _sizeBox = null!;
        private Button _renderButton = null!;
        private Button _saveButton = null!;
        private Button _loadButton = null!;
        private Button _clearButton = null!;
        private ComboBox _modeA = null!;
        private ComboBox _hintingA = null!;
        private ComboBox _renderingA = null!;
        private ComboBox _modeB = null!;
        private ComboBox _hintingB = null!;
        private ComboBox _renderingB = null!;
        private TextBlock _statsText = null!;
        private Image _resultImage = null!;

        private SKBitmap? _lastA;
        private SKBitmap? _referenceB;
        private string? _referenceName;

        public AbDiffView()
        {
            AvaloniaXamlLoader.Load(this);

            _textBox = this.FindControl<TextBox>("TextBox")!;
            _fontBox = this.FindControl<TextBox>("FontBox")!;
            _sizeBox = this.FindControl<NumericUpDown>("SizeBox")!;
            _renderButton = this.FindControl<Button>("RenderButton")!;
            _saveButton = this.FindControl<Button>("SaveButton")!;
            _loadButton = this.FindControl<Button>("LoadButton")!;
            _clearButton = this.FindControl<Button>("ClearButton")!;
            _modeA = this.FindControl<ComboBox>("ModeA")!;
            _hintingA = this.FindControl<ComboBox>("HintingA")!;
            _renderingA = this.FindControl<ComboBox>("RenderingA")!;
            _modeB = this.FindControl<ComboBox>("ModeB")!;
            _hintingB = this.FindControl<ComboBox>("HintingB")!;
            _renderingB = this.FindControl<ComboBox>("RenderingB")!;
            _statsText = this.FindControl<TextBlock>("StatsText")!;
            _resultImage = this.FindControl<Image>("ResultImage")!;

            foreach (var box in new[] { _modeA, _modeB })
            {
                box.ItemsSource = s_modes;
            }

            foreach (var box in new[] { _hintingA, _hintingB })
            {
                box.ItemsSource = Enum.GetValues<TextHintingMode>();
                box.SelectedItem = TextHintingMode.Unspecified;
            }

            foreach (var box in new[] { _renderingA, _renderingB })
            {
                box.ItemsSource = Enum.GetValues<TextRenderingMode>();
                box.SelectedItem = TextRenderingMode.Unspecified;
            }

            _modeA.SelectedIndex = 0;   // A: Managed
            _modeB.SelectedIndex = 1;   // B: Backend

            _renderButton.Click += (_, _) => Render();
            _saveButton.Click += async (_, _) => await SaveReferenceAsync();
            _loadButton.Click += async (_, _) => await LoadReferenceAsync();
            _clearButton.Click += (_, _) =>
            {
                _referenceB?.Dispose();
                _referenceB = null;
                _referenceName = null;
                _clearButton.IsEnabled = false;
                Render();
            };
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (_resultImage.Source is null)
            {
                Render();
            }
        }

        private void Render()
        {
            if (AvaloniaLocator.Current.GetService<FontManagerOptions>() is not { } options)
            {
                _statsText.Text = "FontManagerOptions not registered - cannot switch modes.";
                return;
            }

            var text = _textBox.Text ?? string.Empty;
            var font = _fontBox.Text ?? "Segoe UI";
            var size = (double)(_sizeBox.Value ?? 13);

            var savedMode = options.TextRasterizationMode;

            SKBitmap a;
            SKBitmap? b = null;

            try
            {
                a = RenderSide(options, text, font, size,
                    (string?)_modeA.SelectedItem, _hintingA, _renderingA);

                if (_referenceB is null)
                {
                    b = RenderSide(options, text, font, size,
                        (string?)_modeB.SelectedItem, _hintingB, _renderingB);
                }
            }
            finally
            {
                options.TextRasterizationMode = savedMode;
            }

            _lastA?.Dispose();
            _lastA = a;

            var compareB = _referenceB ?? b!;

            ShowDiff(a, compareB, disposeB: _referenceB is null ? b : null);
        }

        private static SKBitmap RenderSide(FontManagerOptions options, string text, string font,
            double size, string? mode, ComboBox hintingBox, ComboBox renderingBox)
        {
            options.TextRasterizationMode = mode == "Backend"
                ? TextRasterizationMode.Backend
                : TextRasterizationMode.Managed;

            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(font),
                FontSize = size,
                Foreground = Brushes.Black,
            };

            TextOptions.SetTextHintingMode(textBlock,
                hintingBox.SelectedItem is TextHintingMode hinting ? hinting : TextHintingMode.Unspecified);
            TextOptions.SetTextRenderingMode(textBlock,
                renderingBox.SelectedItem is TextRenderingMode rendering ? rendering : TextRenderingMode.Unspecified);

            var host = new Border
            {
                Background = Brushes.White,
                Child = textBlock,
                Padding = new Thickness(8),
            };

            // Runs are created during layout, after the mode was set - that is the whole
            // A/B mechanism.
            host.Measure(Avalonia.Size.Infinity);
            host.Arrange(new Rect(host.DesiredSize));

            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(host.DesiredSize.Width)),
                Math.Max(1, (int)Math.Ceiling(host.DesiredSize.Height)));

            using var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));

            target.Render(host);

            using var stream = new MemoryStream();

            target.Save(stream);
            stream.Position = 0;

            return SKBitmap.Decode(stream);
        }

        private void ShowDiff(SKBitmap a, SKBitmap b, SKBitmap? disposeB)
        {
            const int zoom = 3;
            const int gap = 14;

            var width = Math.Min(a.Width, b.Width);
            var height = Math.Min(a.Height, b.Height);

            long squares = 0;
            var differing = 0;
            var max = 0;

            var heat = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var overlay = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pa = a.GetPixel(x, y);
                    var pb = b.GetPixel(x, y);
                    var dr = Math.Abs(pa.Red - pb.Red);
                    var dg = Math.Abs(pa.Green - pb.Green);
                    var db = Math.Abs(pa.Blue - pb.Blue);
                    var delta = Math.Max(dr, Math.Max(dg, db));

                    squares += (long)dr * dr + (long)dg * dg + (long)db * db;
                    max = Math.Max(max, delta);

                    if (delta > 2)
                    {
                        differing++;
                    }

                    heat.SetPixel(x, y, delta switch
                    {
                        0 => SKColors.White,
                        <= 8 => new SKColor(0xFF, 0xE8, 0x90),
                        <= 32 => new SKColor(0xF5, 0xA6, 0x23),
                        _ => new SKColor(0xD4, 0x22, 0x22),
                    });

                    // Overlay: A ink red, B ink blue, shared ink dark.
                    var inkA = (byte)(255 - (pa.Red + pa.Green + pa.Blue) / 3);
                    var inkB = (byte)(255 - (pb.Red + pb.Green + pb.Blue) / 3);

                    overlay.SetPixel(x, y, new SKColor(
                        (byte)(255 - inkB),
                        (byte)(255 - Math.Max(inkA, inkB)),
                        (byte)(255 - inkA)));
                }
            }

            var rmse = Math.Sqrt(squares / (3.0 * width * height));
            var percent = 100.0 * differing / (width * height);

            var reference = _referenceName is null ? "" : $"  |  B = {_referenceName}";
            var mismatch = a.Width != b.Width || a.Height != b.Height
                ? "  |  size mismatch, compared intersection"
                : "";

            _statsText.Text = FormattableString.Invariant(
                $"RMSE {rmse:0.00}  |  {differing} differing pixels ({percent:0.00}%)  |  max channel delta {max}{reference}{mismatch}");

            // Compose: A, B, overlay, heat map at 3x nearest.
            var panelW = width * zoom;
            var panelH = height * zoom;
            var composed = new SKBitmap(new SKImageInfo(panelW * 4 + gap * 3, panelH + 22,
                SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(composed))
            using (var font = new SKFont(SKTypeface.Default, 12))
            using (var label = new SKPaint { Color = SKColors.Black })
            {
                canvas.Clear(SKColors.White);

                var titles = new[] { "A", "B", "overlay (A red / B blue)", "difference heat map" };
                var images = new[] { a, b, overlay, heat };

                for (var i = 0; i < 4; i++)
                {
                    var x = i * (panelW + gap);

                    canvas.DrawText(titles[i], x, 14, SKTextAlign.Left, font, label);

                    using var image = SKImage.FromBitmap(images[i]);

                    canvas.DrawImage(image, new SKRect(x, 22, x + panelW, 22 + panelH),
                        new SKSamplingOptions(SKFilterMode.Nearest));
                }
            }

            var previous = _resultImage.Source as IDisposable;

            using (composed)
            using (var image = SKImage.FromBitmap(composed))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                _resultImage.Source = new Bitmap(stream);
            }

            previous?.Dispose();
            heat.Dispose();
            overlay.Dispose();
            disposeB?.Dispose();
        }

        private async System.Threading.Tasks.Task SaveReferenceAsync()
        {
            if (_lastA is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "text-reference.png",
                FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
            });

            if (file is null)
            {
                return;
            }

            using var image = SKImage.FromBitmap(_lastA);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await using var stream = await file.OpenWriteAsync();

            data.SaveTo(stream);
        }

        private async System.Threading.Tasks.Task LoadReferenceAsync()
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImagePng },
            });

            if (files.Count != 1)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();

            await stream.CopyToAsync(memory);
            memory.Position = 0;

            _referenceB?.Dispose();
            _referenceB = SKBitmap.Decode(memory);
            _referenceName = files[0].Name;
            _clearButton.IsEnabled = true;
            Render();
        }
    }
}
