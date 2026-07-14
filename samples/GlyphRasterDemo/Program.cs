using Avalonia;
using Avalonia.Media;

namespace GlyphRasterDemo
{
    public class Program
    {
        static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                // The demo starts on the managed rasterization stack; the in-app toggle flips
                // this at runtime for side-by-side validation against the backend stack.
                .With(new FontManagerOptions { TextRasterizationMode = TextRasterizationMode.Managed })
                .LogToTrace();
    }
}
