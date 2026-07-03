using System;
using System.Linq;
using Avalonia;

namespace ImagingDemo
{
    internal static class Program
    {
        // Whether the user asked for the ImageSharp backend on the command line.
        // The window reads this to explain a Skia fallback when ImageSharp was not
        // compiled in (no license key at build time).
        public static bool RequestedImageSharp { get; private set; }

        // True when the ImageSharp backend was referenced at build time.
        public static bool ImageSharpCompiledIn =>
#if IMAGESHARP_BACKEND
            true;
#else
            false;
#endif

        [STAThread]
        public static void Main(string[] args)
        {
            var builder = BuildAvaloniaApp(args);

            // Headless verification path: set up the platform (so the imaging and render
            // backends are bound) without starting the UI, run the operations, and exit.
            if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                builder.SetupWithoutStarting();
                Environment.Exit(SelfTest.Run());
                return;
            }

            builder.StartWithClassicDesktopLifetime(args);
        }

        // Parameterless overload for the XAML previewer / designer.
        public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(Array.Empty<string>());

        public static AppBuilder BuildAvaloniaApp(string[] args)
        {
            RequestedImageSharp = WantsImageSharp(args);

            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .LogToTrace();

#if IMAGESHARP_BACKEND
            // UseImageSharpImaging registers an explicit backend eagerly; because a
            // default registration never replaces an existing binding, this wins over
            // the Skia default that UsePlatformDetect binds later. Leaving this call out
            // keeps the default SkiaSharp backend.
            if (RequestedImageSharp)
                builder = builder.UseImageSharpImaging();
#endif

            return builder;
        }

        private static bool WantsImageSharp(string[] args) =>
            args.Any(a =>
                string.Equals(a, "--imaging=imagesharp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--imagesharp", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Environment.GetEnvironmentVariable("AVALONIA_IMAGING"), "imagesharp",
                StringComparison.OrdinalIgnoreCase);
    }
}
