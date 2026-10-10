using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TextShowcase
{
    internal sealed class App : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var options = ShowcaseOptions.Current;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var view = new ShowcaseView(options);
                var window = new Window
                {
                    Title = "Avalonia text showcase",
                    Content = view,
                    Background = Brushes.White,
                    Width = 1920,
                    Height = 1080,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };

                if (!options.Windowed)
                {
                    window.WindowState = WindowState.FullScreen;
                }

                view.ExitRequested += code => desktop.Shutdown(code);
                desktop.MainWindow = window;
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                single.MainView = new ShowcaseView(options);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
