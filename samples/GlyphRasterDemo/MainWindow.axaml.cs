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

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _host = this.FindControl<ContentControl>("Host")!;
            _modeText = this.FindControl<TextBlock>("ModeText")!;

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
            // that is the whole validation mechanism: flip, rebuild, compare.
            _host.Content = new DemoPage();
        }
    }
}
