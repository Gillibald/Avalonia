using Avalonia.Native.Interop;
using Avalonia.Platform;

namespace Avalonia.Native
{
    public static class EmbeddedTopLevelImpl
    {
        public static ITopLevelImpl Create()
        {
            var factory = AvaloniaLocator.Current.GetRequiredService<IAvaloniaNativeFactory>();

            var topLevel = new TopLevelImpl(factory);
            
            using (var e = new TopLevelImpl.TopLevelEvents(topLevel))
            {
                topLevel.Init(new MacOSTopLevelHandle(factory.CreateTopLevel(e)), factory.CreateScreens());
            }

            return topLevel;
        }
    }
}
