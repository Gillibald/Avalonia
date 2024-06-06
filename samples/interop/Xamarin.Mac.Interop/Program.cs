using Avalonia;

namespace Xamarin.Mac.Interop
{
    public class Program
    {
        static void Main(string[] args)
        {
            BuildAvaloniaApp()
                .SetupWithoutStarting();
            
            NSApplication.Init();
            NSApplication.Main(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .With(new AvaloniaNativePlatformOptions
                {
                    AvaloniaNativeLibraryPath = "/Users/benediktstebner/RiderProjects/Avalonia/native/Avalonia.Native/src/OSX/DerivedData/Avalonia.Native.OSX/Build/Products/Debug/libAvalonia.Native.OSX.dylib"
                })
                .UseSkia()
                .UseAvaloniaNative()
                .LogToTrace();
    }
}
