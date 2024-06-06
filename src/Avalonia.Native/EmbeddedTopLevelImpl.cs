using Avalonia.Native.Interop;

namespace Avalonia.Native
{
    internal class EmbeddedTopLevelImpl : TopLevelImpl
    {
        public EmbeddedTopLevelImpl(IAvaloniaNativeFactory factory) : base(factory)
        {
            using (var e = new TopLevelEvents(this))
            {
                Init(new MacOSTopLevelHandle(factory.CreateTopLevel(e)), factory.CreateScreens());
            }
        }
    }
}
