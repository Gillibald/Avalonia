using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TextTour
{
    internal sealed class App : Application
    {
        private readonly TourOptions _options;

        public App(TourOptions options)
        {
            _options = options;
        }

        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (_options.ExportDirectory is { } directory)
                {
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var exporter = new StepExporter(_options, directory);
                    exporter.Completed += code => desktop.Shutdown(code);
                    exporter.Start();
                }
                else
                {
                    desktop.MainWindow = new TourWindow(_options);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
