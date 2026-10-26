using System;
using Avalonia;

namespace TextTour
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            TourOptions options;

            try
            {
                options = TourOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.Write(TourOptions.Usage);
                return 2;
            }

            if (options.Help)
            {
                Console.Write(TourOptions.Usage);
                return 0;
            }

            return BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(Array.Empty<string>());
        }

        public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(new TourOptions());

        private static AppBuilder BuildAvaloniaApp(TourOptions options)
        {
            return AppBuilder.Configure(() => new App(options))
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
