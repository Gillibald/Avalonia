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
                // Registered so rasterization tooling (the A/B view) can flip the mode per
                // render; the mode itself stays at the framework default (Managed).
                .With(new FontManagerOptions())
#if DEBUG
                .WithDeveloperTools()
#endif
                .LogToTrace();
        }
    }
}
