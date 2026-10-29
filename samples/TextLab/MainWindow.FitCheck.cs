using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TextLab
{
    public partial class MainWindow
    {
        /// <summary>
        /// The client sizes every view is laid out at: a MacBook window zoomed to the visible frame
        /// (1440x799 points less the title bar) and a 1080p projector.
        /// </summary>
        private static readonly Size[] s_fitSizes = { new(1440, 769), new(1920, 1080) };

        /// <summary>
        /// Lays every view out at each <see cref="s_fitSizes"/> entry in light and dark, writes one
        /// PNG per combination into <paramref name="directory"/> and a report of every scroll
        /// viewer whose content exceeds its viewport. Scroll viewers marked with the
        /// <c>data</c> class, or inside a control marked with it, hold open-ended data and are
        /// reported as allowed. Closes the window when done.
        /// </summary>
        private async Task RunFitCheckAsync(string directory)
        {
            Directory.CreateDirectory(directory);

            var report = new StringBuilder("size\ttheme\tview\tscroller\tviewport\textent\toverflow\tallowed\n");
            var root = (Control)Content!;

            foreach (var dark in new[] { false, true })
            {
                _themeToggle.IsChecked = dark;

                // A theme switch rebuilds every view's figures, some of them on timers.
                await Task.Delay(1500);

                foreach (var (name, show) in FitCheckViews())
                {
                    show();

                    foreach (var size in s_fitSizes)
                    {
                        // The window itself is kept inside the screen, so the content is laid out at
                        // the requested size directly and inspected before the next layout pass.
                        await SettleAsync();
                        root.Measure(size);
                        root.Arrange(new Rect(size));

                        var label = string.Create(CultureInfo.InvariantCulture,
                            $"{size.Width:0}x{size.Height:0}\t{(dark ? "dark" : "light")}\t{name}");
                        var overflowing = 0;

                        foreach (var scroller in root.GetVisualDescendants().OfType<ScrollViewer>())
                        {
                            if (!scroller.IsEffectivelyVisible || scroller.Viewport.Width <= 0)
                            {
                                continue;
                            }

                            var overflowX = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
                            var overflowY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);

                            if (overflowX < 1 && overflowY < 1)
                            {
                                continue;
                            }

                            overflowing++;
                            report.Append(CultureInfo.InvariantCulture,
                                $"{label}\t{Describe(scroller)}\t{scroller.Viewport.Width:0}x{scroller.Viewport.Height:0}\t" +
                                $"{scroller.Extent.Width:0}x{scroller.Extent.Height:0}\t{overflowX:0}x{overflowY:0}\t" +
                                $"{(IsData(scroller) ? "yes" : "no")}\n");
                        }

                        if (overflowing == 0)
                        {
                            report.Append(label).Append("\t-\t-\t-\t0x0\t-\n");
                        }

                        var scaling = RenderScaling;
                        using var bitmap = new RenderTargetBitmap(
                            new PixelSize((int)(size.Width * scaling), (int)(size.Height * scaling)),
                            new Vector(96 * scaling, 96 * scaling));
                        // The content draws over the window background, which a capture of the
                        // content alone lacks.
                        var panel = root as Panel;
                        var background = panel?.Background;

                        if (panel is not null && this.TryFindResource("SystemRegionBrush", ActualThemeVariant, out var brush))
                        {
                            panel.Background = brush as Avalonia.Media.IBrush;
                        }

                        bitmap.Render(root);

                        if (panel is not null)
                        {
                            panel.Background = background;
                        }
                        bitmap.Save(Path.Combine(directory, string.Create(CultureInfo.InvariantCulture,
                            $"{name}-{size.Width:0}x{size.Height:0}-{(dark ? "dark" : "light")}.png")),
                            PngBitmapEncoderOptions.Default);
                    }
                }
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "fit-report.tsv"), report.ToString());
            Close();
        }

        /// <summary>Every view and state the check visits, with the action that shows it.</summary>
        private IEnumerable<(string Name, Action Show)> FitCheckViews()
        {
            yield return ("specimen", () => _nav.SelectedItem = NavSpecimen);
            yield return ("glyphs", () =>
            {
                _nav.SelectedItem = NavGlyphs;
                ShowInspector(false);
            });
            yield return ("glyph-inspector", () =>
            {
                _nav.SelectedItem = NavGlyphs;

                if (RasterizationView.ResolveTypeface(CurrentFamilyName()) is { } typeface)
                {
                    _raster.ShowGlyph(typeface, typeface.CharacterToGlyphMap['g'], "'g'");
                }

                ShowInspector(true);
            });
            yield return ("color-inspector", () =>
            {
                _nav.SelectedItem = NavGlyphs;

                if ((RasterizationView.ResolveTypeface("Apple Color Emoji") ??
                     RasterizationView.ResolveTypeface("Segoe UI Emoji") ??
                     RasterizationView.ResolveTypeface("Noto Color Emoji")) is { } emoji)
                {
                    _colorInspector.ShowGlyph(emoji, emoji.CharacterToGlyphMap[0x1F98A], "U+1F98A");
                }

                ShowColorInspector();
            });
            yield return ("variable-fonts", () => _nav.SelectedItem = NavVariable);
            yield return ("font-info", () => _nav.SelectedItem = NavFontInfo);
            yield return ("waterfall", () => _nav.SelectedItem = NavWaterfall);
            yield return ("fringes", () => _nav.SelectedItem = NavFringes);
            yield return ("ab-diff", () => _nav.SelectedItem = NavAbDiff);
            yield return ("hit-testing-buffer", () =>
            {
                _nav.SelectedItem = NavHitTesting;
                _tabs.SelectedIndex = 0;
            });
            yield return ("hit-testing-hits", () =>
            {
                _nav.SelectedItem = NavHitTesting;
                _tabs.SelectedIndex = 1;
            });

            for (var page = 0; page < _learn.PageCount; page++)
            {
                var index = page;
                yield return (string.Create(CultureInfo.InvariantCulture, $"pipeline-{index + 1}"), () =>
                {
                    _nav.SelectedItem = NavPipeline;
                    _learn.ShowPage(index);
                });
            }
        }

        private string CurrentFamilyName() =>
            (_font.SelectedItem as Avalonia.Media.FontFamily)?.Name ?? Avalonia.Media.FontManager.Current.DefaultFontFamily.Name;

        /// <summary>Lets queued renders and layout passes run before the view is measured.</summary>
        private static async Task SettleAsync()
        {
            for (var i = 0; i < 4; i++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(200);
            }
        }

        private static bool IsData(Visual scroller) =>
            scroller.GetSelfAndVisualAncestors().OfType<StyledElement>().Any(e => e.Classes.Contains("data"));

        /// <summary>The scroll viewer's nearest named ancestor and what it scrolls, for the report.</summary>
        private static string Describe(ScrollViewer scroller)
        {
            var owner = scroller.GetSelfAndVisualAncestors().OfType<Control>()
                .FirstOrDefault(c => !string.IsNullOrEmpty(c.Name) && c != scroller);
            var content = (scroller.Presenter as ScrollContentPresenter)?.Child as Control;
            var what = content is null ? "" : content.Name is { Length: > 0 } n ? n : content.GetType().Name;

            return $"{owner?.Name ?? owner?.GetType().Name ?? "?"}/{what}";
        }
    }
}
