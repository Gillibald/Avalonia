using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TextShowcase.Diagnostics;

namespace TextShowcase.Panes
{
    /// <summary>
    /// One side of the comparison: a label bar naming the pipeline, the scene content under its
    /// rasterization mode, and an optional HUD line. The chrome itself is not part of the
    /// comparison and renders with the application's global mode.
    /// </summary>
    internal sealed class PaneView : Border
    {
        private static readonly IBrush s_managedBadge = new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E));
        private static readonly IBrush s_backendBadge = new SolidColorBrush(Color.FromRgb(0x6D, 0x3F, 0xC0));
        private static readonly IBrush s_diffBadge = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));

        private readonly Border _badge;
        private readonly TextBlock _badgeText;
        private readonly TextBlock _title;
        private readonly TextBlock _note;
        private readonly Border _contentHost;
        private readonly TextBlock _hud;
        private readonly Border _verdict;
        private readonly TextBlock _verdictText;
        private Control? _content;
        private TextRasterizationMode? _mode;
        private string _badgeLabel = "";
        private string _titleLabel = "";
        private string? _noteLabel;

        public PaneView()
        {
            Background = Brushes.White;
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xDD));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(10);
            ClipToBounds = true;

            _badgeText = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _badge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 3),
                Child = _badgeText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _title = new TextBlock
            {
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                Foreground = Scenes.Ui.Ink,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            _note = new TextBlock
            {
                FontSize = 14,
                Foreground = Scenes.Ui.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var labelBar = new DockPanel
            {
                Margin = new Thickness(20, 14, 20, 10),
                LastChildFill = true,
            };
            DockPanel.SetDock(_badge, Dock.Left);
            DockPanel.SetDock(_title, Dock.Left);
            labelBar.Children.Add(_badge);
            labelBar.Children.Add(_title);
            labelBar.Children.Add(_note);

            _contentHost = new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(28, 12, 28, 16),
                ClipToBounds = true,
            };

            _hud = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3B)),
                TextWrapping = TextWrapping.Wrap,
            };
            var hudBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                Padding = new Thickness(20, 8),
                Child = _hud,
                IsVisible = false,
            };

            _verdictText = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            };
            _verdict = new Border
            {
                Padding = new Thickness(20, 8),
                Child = _verdictText,
                IsVisible = false,
            };

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(labelBar, Dock.Top);
            DockPanel.SetDock(_verdict, Dock.Top);
            DockPanel.SetDock(hudBar, Dock.Bottom);
            layout.Children.Add(labelBar);
            layout.Children.Add(_verdict);
            layout.Children.Add(hudBar);
            layout.Children.Add(_contentHost);

            Child = layout;
            HudBar = hudBar;
        }

        /// <summary>The visual that captures and diffs render: the content area with its background.</summary>
        public Border ContentHost => _contentHost;

        public Border HudBar { get; }

        public bool ShowsDiff { get; private set; }

        public void SetContent(Control content, TextRasterizationMode? mode, string badge, string title, string? note)
        {
            _content = content;
            _mode = mode;
            _badgeLabel = badge;
            _titleLabel = title;
            _noteLabel = note;
            PaneMode.SetMode(_contentHost, mode);
            ShowsDiff = false;
            _verdict.IsVisible = false;
            ApplyLabels();
            _contentHost.Child = content;
        }

        public void ShowDiff(Bitmap heatMap, DiffResult result)
        {
            ShowsDiff = true;
            _badge.Background = s_diffBadge;
            _badgeText.Text = "DIFF";
            _title.Text = "Managed vs Backend, offscreen grayscale";
            _note.Text = null;

            var (verdict, tint) = result.Verdict;
            _verdict.Background = new SolidColorBrush(tint);
            _verdictText.Text = FormattableString.Invariant(
                $"{verdict}.  {result.DifferingPixels:N0} pixels differ ({100.0 * result.DifferingPixels / Math.Max(1, result.InkPixels):0.#} % of ink), max channel delta {result.MaxDelta}, RMSE {result.Rmse:0.00}");
            _verdict.IsVisible = true;

            _contentHost.Child = new Image
            {
                Source = heatMap,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                // The heat map is the host's own size; undo the host padding so it lines up.
                Margin = new Thickness(-_contentHost.Padding.Left, -_contentHost.Padding.Top, 0, 0),
            };
        }

        public void HideDiff()
        {
            if (!ShowsDiff)
            {
                return;
            }

            ShowsDiff = false;
            _verdict.IsVisible = false;
            ApplyLabels();
            _contentHost.Child = _content;
        }

        private void ApplyLabels()
        {
            _badgeText.Text = _badgeLabel;
            _badge.Background = _mode == TextRasterizationMode.Managed ? s_managedBadge : s_backendBadge;
            _title.Text = _titleLabel;
            _note.Text = _noteLabel;
        }

        public void SetHud(string? text)
        {
            HudBar.IsVisible = text is not null;
            _hud.Text = text;
        }
    }
}
