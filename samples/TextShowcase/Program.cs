using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace TextShowcase
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                ShowcaseOptions.Current = ShowcaseOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.Write(ShowcaseOptions.Usage);
                return 2;
            }

            if (ShowcaseOptions.Current.Help)
            {
                Console.Write(ShowcaseOptions.Usage);
                return 0;
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            var options = ShowcaseOptions.Current;
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                // Registered so the panes can scope the mode; the chrome keeps the platform default.
                .With(new FontManagerOptions())
                .LogToTrace();

            if (options.Render != "default")
            {
                builder = builder.With(new Win32PlatformOptions { RenderingMode = ParseWin32Rendering(options.Render) });
            }

            return builder;
        }

        private static IReadOnlyList<Win32RenderingMode> ParseWin32Rendering(string value) => value switch
        {
            "angle" => new[] { Win32RenderingMode.AngleEgl },
            "wgl" => new[] { Win32RenderingMode.Wgl },
            "software" => new[] { Win32RenderingMode.Software },
            "vulkan" => new[] { Win32RenderingMode.Vulkan },
            _ => throw new ArgumentException($"Unknown --render '{value}'.")
        };
    }
}
