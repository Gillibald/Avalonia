using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace TextLab
{
    /// <summary>
    /// The specimen: a screenshot-ready page of live application text - headline, weights,
    /// size ramp, an OpenType feature playground and a script gallery - all rendered by
    /// whatever the global raster selector picks. Nothing here is a bitmap figure; this is
    /// the app's own text stack showing itself off.
    /// </summary>
    public partial class ShowcaseView : UserControl
    {
        private static readonly (string Label, FontWeight Weight)[] s_weights =
        {
            ("Light", FontWeight.Light),
            ("Regular", FontWeight.Normal),
            ("SemiBold", FontWeight.SemiBold),
            ("Bold", FontWeight.Bold),
            ("Black", FontWeight.Black),
        };

        private static readonly string[] s_features =
            { "liga", "dlig", "smcp", "onum", "tnum", "zero", "frac", "calt", "ss01" };

        private static readonly (string Script, string Sample, bool RightToLeft)[] s_scripts =
        {
            ("Latin", "The quick brown fox jumps over the lazy dog", false),
            ("Cyrillic", "Съешь же ещё этих мягких французских булок", false),
            ("Greek", "Θέλει αρετή και τόλμη η ελευθερία", false),
            ("Arabic", "أبجد هوز حطي كلمن سعفص قرشت", true),
            ("Devanagari", "श्रुति स्मृति पुराण", false),
            ("CJK + Kana + Hangul", "永字八法 あいうえお 한글", false),
            ("Emoji", "🌈 🌊 🎪 🦊 🚀", false),
        };

        private ContentControl _host = null!;
        private string _familyName = "Segoe UI";
        private readonly HashSet<string> _activeFeatures = new();
        private TextBlock? _playground;

        public ShowcaseView()
        {
            AvaloniaXamlLoader.Load(this);

            _host = this.FindControl<ContentControl>("Host")!;
        }

        /// <summary>The app-global family. Rebuilds the page - text controls cache their
        /// formatted runs, so a rebuild is also how the raster toggle reaches this view.</summary>
        public void SetContext(string familyName)
        {
            _familyName = familyName;
            Rebuild();
        }

        /// <summary>Rebuilds the live text so glyph runs are recreated - the host calls this
        /// when the rasterization mode flips.</summary>
        public void Refresh() => Rebuild();

        private void Rebuild()
        {
            var family = new FontFamily(_familyName);
            var mode = AvaloniaLocator.Current.GetService<FontManagerOptions>()?.TextRasterizationMode;
            var pipeline = mode == TextRasterizationMode.Backend
                ? "the render backend's text stack"
                : "Avalonia's managed glyph pipeline";

            var root = new StackPanel { Spacing = 16, MaxWidth = 900 };

            root.Children.Add(new TextBlock
            {
                Text = _familyName,
                FontFamily = family,
                FontSize = 46,
                FontWeight = FontWeight.SemiBold,
            });

            root.Children.Add(new TextBlock
            {
                Text = $"Every line on this page is live application text rendered through {pipeline}.",
                FontSize = 12,
                Opacity = 0.65,
                Margin = new Thickness(0, -10, 0, 0),
            });

            var weights = new WrapPanel();

            foreach (var (label, weight) in s_weights)
            {
                weights.Children.Add(new TextBlock
                {
                    Text = $"Aa {label}",
                    FontFamily = family,
                    FontSize = 22,
                    FontWeight = weight,
                    Margin = new Thickness(0, 0, 22, 4),
                });
            }

            root.Children.Add(weights);

            var ramp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };

            foreach (var size in new[] { 12, 16, 22, 30, 42, 58 })
            {
                ramp.Children.Add(new TextBlock
                {
                    Text = "Ag",
                    FontFamily = family,
                    FontSize = size,
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
            }

            root.Children.Add(ramp);

            root.Children.Add(new TextBlock
            {
                Text = "Typography gives language a visible body. A text engine earns its keep in the " +
                       "details: consistent stems at small sizes, even color across a paragraph, correct " +
                       "joining behavior in scripts far from Latin, and pixel output that survives being " +
                       "put under a loupe.",
                FontFamily = family,
                FontSize = 15,
                MaxWidth = 680,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(SectionTitle("OpenType features"));
            root.Children.Add(new TextBlock
            {
                Text = "Chips add explicit features on top of the shaper's defaults; the line below re-shapes live.",
                FontSize = 11,
                Opacity = 0.65,
            });

            var chips = new WrapPanel();

            foreach (var feature in s_features)
            {
                var chip = new ToggleButton
                {
                    Content = feature,
                    FontSize = 12,
                    IsChecked = _activeFeatures.Contains(feature),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                AutomationProperties.SetName(chip, $"{feature} feature");

                chip.IsCheckedChanged += (_, _) =>
                {
                    if (chip.IsChecked == true)
                    {
                        _activeFeatures.Add(feature);
                    }
                    else
                    {
                        _activeFeatures.Remove(feature);
                    }

                    ApplyFeatures();
                };

                chips.Children.Add(chip);
            }

            root.Children.Add(chips);

            _playground = new TextBlock
            {
                Text = "Official waffles suffice - 0123 & 7/8 fjord flick",
                FontFamily = family,
                FontSize = 26,
            };
            ApplyFeatures();
            root.Children.Add(_playground);

            root.Children.Add(SectionTitle("Script gallery"));
            root.Children.Add(new TextBlock
            {
                Text = "Rows the family does not cover fall back through the font manager - fallback is part of the demo.",
                FontSize = 11,
                Opacity = 0.65,
            });

            foreach (var (script, sample, rtl) in s_scripts)
            {
                var row = new DockPanel();

                var label = new TextBlock
                {
                    Text = script,
                    FontSize = 11,
                    Opacity = 0.6,
                    Width = 130,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);

                row.Children.Add(new TextBlock
                {
                    Text = sample,
                    FontFamily = family,
                    FontSize = 20,
                    FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });

                root.Children.Add(row);
            }

            _host.Content = root;
        }

        private void ApplyFeatures()
        {
            if (_playground is null)
            {
                return;
            }

            var features = _activeFeatures.Count == 0
                ? null
                : FontFeatureCollection.Parse(string.Join(' ', _activeFeatures));

            _playground.SetValue(TextElement.FontFeaturesProperty, features);
        }

        private static TextBlock SectionTitle(string text) => new()
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
    }
}
