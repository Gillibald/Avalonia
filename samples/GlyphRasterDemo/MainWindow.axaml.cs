using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace GlyphRasterDemo
{
    public partial class MainWindow : Window
    {
        private ContentControl _host = null!;
        private TextBlock _modeText = null!;
        private ComboBox _renderingModeBox = null!;
        private ComboBox _hintingModeBox = null!;
        private bool _initialized;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _host = this.FindControl<ContentControl>("Host")!;
            _modeText = this.FindControl<TextBlock>("ModeText")!;
            _renderingModeBox = this.FindControl<ComboBox>("RenderingModeBox")!;
            _hintingModeBox = this.FindControl<ComboBox>("HintingModeBox")!;

            _renderingModeBox.ItemsSource = System.Enum.GetValues<TextRenderingMode>();
            _renderingModeBox.SelectedItem = TextRenderingMode.Unspecified;
            _hintingModeBox.ItemsSource = System.Enum.GetValues<TextHintingMode>();
            _hintingModeBox.SelectedItem = TextHintingMode.Unspecified;
            _initialized = true;

            Reload();
        }

        private void OnTintTiersChanged(object? sender, RoutedEventArgs e)
        {
            // The overlay badges appear on freshly built draws; rebuilding the page is the
            // demo's standard refresh.
            Avalonia.Skia.TextTierDiagnostics.TintTiers = ((CheckBox)sender!).IsChecked == true;
            Reload();
        }

        private void OnTextModeChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            // Applied to the host, so the whole page inherits; rows that set their own value
            // (the modes block) keep overriding, staying a fixed reference.
            TextOptions.SetTextRenderingMode(_host, (TextRenderingMode)_renderingModeBox.SelectedItem!);
            TextOptions.SetTextHintingMode(_host, (TextHintingMode)_hintingModeBox.SelectedItem!);

            Reload();
        }

        private static FontManagerOptions? Options
            => AvaloniaLocator.Current.GetService<FontManagerOptions>();

        private void OnToggleMode(object? sender, RoutedEventArgs e)
        {
            if (Options is not { } options)
            {
                return;
            }

            options.TextRasterizationMode = options.TextRasterizationMode == TextRasterizationMode.Managed
                ? TextRasterizationMode.Backend
                : TextRasterizationMode.Managed;

            Reload();
        }

        private void Reload()
        {
            _modeText.Text = $"mode: {Options?.TextRasterizationMode}";

            // Fresh visuals build fresh glyph runs, and run creation reads the mode live —
            // that is the whole validation mechanism: flip, rebuild, compare. (The Slug switch
            // alone is read per draw and would not strictly need the rebuild.)
            _host.Content = new DemoPage();
        }
    }
}
