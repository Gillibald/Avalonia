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
    /// tier-routing overlay for the window's own rendering. Accepts a character, U+XXXX
    /// codepoint or #id glyph reference, so ligatures and unmapped glyphs are reachable.
    /// </summary>
    public partial class RasterizationView : UserControl
    {
        private TextBox _charBox = null!;
        private TextBox _fontBox = null!;
        private Slider _sizeSlider = null!;
        private TextBlock _sizeText = null!;
        private ComboBox _hintingBox = null!;
        private CheckBox _gammaBox = null!;
        private CheckBox _bgrBox = null!;
        private CheckBox _tintTiersBox = null!;
        private Image _hintingImage = null!;
        private Image _maskImage = null!;
        private Image _lcdImage = null!;
        private Image _slugImage = null!;
        private ContentControl _tierSampleHost = null!;
        private GlyphTypeface? _overrideTypeface;
        private bool _initialized;

        public RasterizationView()
        {
            AvaloniaXamlLoader.Load(this);

            _charBox = this.FindControl<TextBox>("CharBox")!;
            _fontBox = this.FindControl<TextBox>("FontBox")!;
            _sizeSlider = this.FindControl<Slider>("SizeSlider")!;
            _sizeText = this.FindControl<TextBlock>("SizeText")!;
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

            _charBox.TextChanged += (_, _) => Rebuild();
            _fontBox.TextChanged += (_, _) =>
            {
                _overrideTypeface = null;   // a typed family name wins over a pushed instance
                Rebuild();
            };
            _sizeSlider.ValueChanged += (_, _) => Rebuild();
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

        /// <summary>Shows a specific glyph of a specific typeface instance — the drill-down
        /// entry from the shaped buffer and the glyph explorer.</summary>
        public void ShowGlyph(GlyphTypeface typeface, ushort glyphIndex)
        {
            _fontBox.Text = typeface.FamilyName;    // display only; the instance wins
            _overrideTypeface = typeface;           // set after TextChanged cleared it
            _charBox.Text = $"#{glyphIndex}";
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            // Leaving the view must not leave the global overlay on.
            TextTierDiagnostics.TintTiers = false;
            base.OnDetachedFromVisualTree(e);
        }

        private void Rebuild()
        {
            if (!_initialized)
            {
                return;
            }

            var size = (float)_sizeSlider.Value;
            var hinting = _hintingBox.SelectedItem is TextHintingMode mode ? mode : TextHintingMode.Light;

            _sizeText.Text = $"{size:0.#} px";

            var typeface = _overrideTypeface ?? ResolveTypeface(_fontBox.Text ?? "Segoe UI");

            if (typeface is null || !TryParseGlyph(typeface, _charBox.Text, out var glyph, out var label))
            {
                return;
            }

            SetImage(_hintingImage, PipelineFigures.HintingAnatomy(typeface, glyph, label, size, hinting));
            SetImage(_maskImage, PipelineFigures.MaskAnatomy(typeface, glyph, label, "Hamburgefonstiv", size));
            SetImage(_lcdImage, PipelineFigures.ClearTypePipeline(typeface, glyph, label, size,
                _bgrBox.IsChecked == true, _gammaBox.IsChecked == true, hinting));
            SetImage(_slugImage, PipelineFigures.SlugBands(typeface, glyph, label));
        }

        /// <summary>Parses "g" (a character), "U+0067" (a codepoint) or "#74" (a glyph id).</summary>
        private static bool TryParseGlyph(GlyphTypeface typeface, string? text,
            out ushort glyph, out string label)
        {
            glyph = 0;
            label = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();

            if (text.StartsWith('#') && ushort.TryParse(text.AsSpan(1), out var id))
            {
                if (id >= typeface.GlyphCount)
                {
                    return false;
                }

                glyph = id;
                label = $"#{id}";
                return true;
            }

            if ((text.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var codepoint) &&
                typeface.CharacterToGlyphMap.ContainsGlyph(codepoint))
            {
                glyph = typeface.CharacterToGlyphMap[codepoint];
                label = $"U+{codepoint:X4}";
                return true;
            }

            var reference = text[0];

            if (!typeface.CharacterToGlyphMap.ContainsGlyph(reference))
            {
                return false;
            }

            glyph = typeface.CharacterToGlyphMap[reference];
            label = $"'{reference}'";
            return true;
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
