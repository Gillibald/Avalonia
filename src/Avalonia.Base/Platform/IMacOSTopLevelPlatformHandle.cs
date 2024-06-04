using System;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    [Unstable]
    public interface IMacOSTopLevelPlatformHandle
    {
        IntPtr NSView { get; }
        IntPtr GetNSViewRetained();
    }

    [Unstable]
    public interface IMacOSWindowHandle : IMacOSTopLevelPlatformHandle
    {
        IntPtr NSWindow { get; }
        IntPtr GetNSWindowRetained();
    }
}
