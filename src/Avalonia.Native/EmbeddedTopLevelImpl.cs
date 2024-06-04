using System;
using Avalonia.Native.Interop;

namespace Avalonia.Native
{
    internal class EmbeddedTopLevelImpl : TopLevelImpl
    {
        public EmbeddedTopLevelImpl(IAvaloniaNativeFactory factory, IntPtr parentViewHandle) : base(factory)
        {
            using (var e = new TopLevelEvents(this))
            {
                Init(new MacOSTopLevelHandle(factory.CreateEmbeddedTopLevel(e, parentViewHandle)), factory.CreateScreens());
            }
        }
    }
}
