using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using TextStress.Measurement;

namespace TextStress
{
    internal static class Program
    {
        internal static RunOptions Options { get; private set; } = null!;

        [STAThread]
        public static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
            {
                Console.Write(RunOptions.Usage);
                return 0;
            }

            try
            {
                Options = RunOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.Write(RunOptions.Usage);
                return 2;
            }

            ThreadClock.Calibrate();

            if (Options.PendingBatches > 0)
            {
                Avalonia.Skia.DrawingContextImpl.MaxPendingBatches = Options.PendingBatches;
            }

            return BuildAvaloniaApp(Options).StartWithClassicDesktopLifetime(Array.Empty<string>());
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(Options ?? RunOptions.Parse(Array.Empty<string>()));

        private static AppBuilder BuildAvaloniaApp(RunOptions options)
        {
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                // Each process measures one mode; A/B comparisons are separate, interleaved
                // processes, so neither mode inherits the other's caches.
                .With(new FontManagerOptions
                {
                    TextRasterizationMode = options.Mode == "backend"
                        ? TextRasterizationMode.Backend
                        : TextRasterizationMode.Managed
                })
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
