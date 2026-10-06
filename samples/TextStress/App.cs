using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TextStress
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
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var options = Program.Options;
                var window = new Window
                {
                    Title = "TextStress",
                    Width = options.Width,
                    Height = options.Height,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(40, 40),
                    // Keep the window unoccluded so the compositor never skips presenting it.
                    Topmost = options.Frames > 0,
                    Background = Brushes.White
                };

                window.Opened += async (_, _) =>
                {
                    var exitCode = await StressRun.RunAsync(window, options);

                    if (options.Frames > 0 || exitCode != 0)
                    {
                        desktop.Shutdown(exitCode);
                    }
                };

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
