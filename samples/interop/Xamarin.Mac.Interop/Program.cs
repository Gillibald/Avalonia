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
                .UseSkia()
                .UseAvaloniaNative()
                .LogToTrace();
    }
}
