using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using SkiaSharp;

namespace TextTestApp
{
    /// <summary>
    /// The rasterization inspector: live figures for each stage of the managed glyph pipeline
    /// (hinting warps, mask anatomy, ClearType stages, the Slug payload) plus a live
    /// tier-routing overlay for the window's own rendering. Selection-driven: it displays
    /// whatever glyph the explorer or the shaped buffer pushed, at the app's global font
    /// size, so ligatures and unmapped glyphs are reachable through the explorer.
    /// </summary>
    public partial class RasterizationView : UserControl
    {
        private TextBlock _glyphText = null!;
        private ComboBox _hintingBox = null!;
        private CheckBox _gammaBox = null!;
        private CheckBox _bgrBox = null!;
        private CheckBox _tintTiersBox = null!;
        private Image _hintingImage = null!;
        private Image _maskImage = null!;
        private Image _lcdImage = null!;
        private Image _slugImage = null!;
        private ContentControl _tierSampleHost = null!;
        private GlyphTypeface? _typeface;
        private ushort _glyph;
        private string? _label;
        private float _size = 13;
        private bool _initialized;

        public RasterizationView()
        {
            AvaloniaXamlLoader.Load(this);

            _glyphText = this.FindControl<TextBlock>("GlyphText")!;
            _hintingBox = this.FindControl<ComboBox>("HintingBox")!;
            _gammaBox = this.FindControl<CheckBox>("GammaBox")!;
            _bgrBox = this.FindControl<CheckBox>("BgrBox")!;
            _tintTiersBox = this.FindControl<CheckBox>("TintTiersBox")!;
            _hintingImage = this.FindControl<Image>("HintingImage")!;
            _maskImage = this.FindControl<Image>("MaskImage")!;
            _lcdImage = this.FindControl<Image>("LcdImage")!;
            _slugImage = this.FindControl<Image>("SlugImage")!;
            _tierSampleHost = this.FindControl<ContentControl>("TierSampleHost")!;

            _hintingBox.ItemsSource = new[] { TextHintingMode.Light, TextHintingMode.None, TextHintingMode.Strong };
            _hintingBox.SelectedIndex = 0;

            _hintingBox.SelectionChanged += (_, _) => Rebuild();
            _gammaBox.IsCheckedChanged += (_, _) => Rebuild();
            _bgrBox.IsCheckedChanged += (_, _) => Rebuild();
            _tintTiersBox.IsCheckedChanged += (_, _) =>
            {
                TextTierDiagnostics.TintTiers = _tintTiersBox.IsChecked == true;

                // Fresh content builds fresh draws, so the badges appear immediately.
                _tierSampleHost.Content = BuildTierSample();
            };

            _tierSampleHost.Content = BuildTierSample();
            _initialized = true;

            if (Environment.GetEnvironmentVariable("GLYPH_INSPECTOR") == "tint")
            {
                _tintTiersBox.IsChecked = true;
            }

            Rebuild();
        }

        /// <summary>Shows a specific glyph of a specific typeface instance — pushed by the
        /// explorer's selection and the shaped buffer's drill-down.</summary>
        public void ShowGlyph(GlyphTypeface typeface, ushort glyphIndex, string? label = null)
        {
            _typeface = typeface;
            _glyph = glyphIndex;
            _label = label;
            Rebuild();
        }

        /// <summary>The app-global font and size. A typeface change resets the shown glyph to
        /// a default — glyph ids are font-specific and must not carry over silently.</summary>
        public void SetContext(GlyphTypeface? typeface, double size)
        {
            _size = (float)size;

            if (typeface is not null && !ReferenceEquals(typeface, _typeface))
            {
                _typeface = typeface;
                _glyph = typeface.CharacterToGlyphMap.ContainsGlyph('g')
                    ? typeface.CharacterToGlyphMap['g']
                    : (ushort)0;
                _label = _glyph == 0 ? null : "'g'";
            }

            Rebuild();
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            // Leaving the view must not leave the global overlay on.
            TextTierDiagnostics.TintTiers = false;
            base.OnDetachedFromVisualTree(e);
        }

        private void Rebuild()
        {
            if (!_initialized || _typeface is not { } typeface || _glyph >= typeface.GlyphCount)
            {
                return;
            }

            var hinting = _hintingBox.SelectedItem is TextHintingMode mode ? mode : TextHintingMode.Light;
            var size = Math.Clamp(_size, 6, 96);
            var label = _label is null ? $"#{_glyph}" : $"{_label} (#{_glyph})";

            _glyphText.Text = FormattableString.Invariant(
                $"{label} — {typeface.FamilyName}, {size:0.#} px");

            SetImage(_hintingImage, PipelineFigures.HintingAnatomy(typeface, _glyph, label, size, hinting));
            SetImage(_maskImage, PipelineFigures.MaskAnatomy(typeface, _glyph, label, "Hamburgefonstiv", size));
            SetImage(_lcdImage, PipelineFigures.ClearTypePipeline(typeface, _glyph, label, size,
                _bgrBox.IsChecked == true, _gammaBox.IsChecked == true, hinting));
            SetImage(_slugImage, PipelineFigures.SlugBands(typeface, _glyph, label));
        }

        internal static GlyphTypeface? ResolveTypeface(string familyName)
        {
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(familyName), out var resolved) &&
                resolved is GlyphTypeface managed &&
                managed.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase))
            {
                return managed;
            }

            using var skTypeface = SKFontManager.Default.MatchFamily(familyName, SKFontStyle.Normal);

            return skTypeface is null ? null : new GlyphTypeface(new SkiaTypeface(skTypeface, FontSimulations.None));
        }

        private static void SetImage(Image target, SKBitmap figure)
        {
            var previous = target.Source as IDisposable;

            using (figure)
            using (var image = SKImage.FromBitmap(figure))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new MemoryStream(data.ToArray()))
            {
                target.Source = new Bitmap(stream);
            }

            previous?.Dispose();
        }

        private static Control BuildTierSample()
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.OrangeRed, 0),
                    new GradientStop(Colors.RoyalBlue, 1),
                },
            };

            var rotated = new TextBlock
            {
                Text = "Rotated 32 px — the mask triage declines, the vector tier takes it",
                FontSize = 32,
                RenderTransform = new RotateTransform(14),
                RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                Margin = new Thickness(8, 40, 0, 60),
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Body text at 13 px composes through cached run masks — the workhorse tier for axis-aligned UI text.",
                            FontSize = 13,
                        },
                        new TextBlock
                        {
                            Text = "Zg 200 px",
                            FontSize = 200,
                        },
                        rotated,
                        new TextBlock
                        {
                            Text = "Gradient foreground at 24 px — non-solid brushes fall through to the native blob.",
                            FontSize = 24,
                            Foreground = gradient,
                        },
                        new TextBlock
                        {
                            Text = "Legend: green = run masks, magenta = Slug vector tier, orange = native blob.",
                            FontSize = 12,
                            Opacity = 0.7,
                        },
                    },
                },
            };
        }
    }
}
