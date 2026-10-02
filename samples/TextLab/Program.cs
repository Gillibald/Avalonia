using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;

namespace TextLab
{
    static class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            TypeDescriptor.AddAttributes(typeof(FontFeatureCollection), new TypeConverterAttribute(typeof(FontFeatureCollectionConverter)));

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The lab inspects the managed rasterization pipeline, so it starts on Managed on
                // every platform rather than the platform default; the raster selector and the A/B
                // view flip this registered instance at runtime.
                .With(new FontManagerOptions { TextRasterizationMode = TextRasterizationMode.Managed })
#if DEBUG
                .WithDeveloperTools()
#endif
                .LogToTrace();
        }
    }
}
