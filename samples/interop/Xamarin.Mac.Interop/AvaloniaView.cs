using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Native;
using Avalonia.Platform;

namespace Xamarin.Mac.Interop;

public class AvaloniaView : IDisposable
{
    private readonly EmbeddableControlRoot _topLevel;
    private Control? _content;

    public AvaloniaView()
    {
        _topLevel = new EmbeddableControlRoot();
        
        if (_topLevel.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle)
        {
            AvnView = ObjCRuntime.Runtime.GetNSObject(handle.NSView) as NSView;
        }
    }
    
    public NSView? AvnView { get; }

    public Control? Content
    {
        get => _content;
        set
        {
            _content = value;
            
            _topLevel.Content = _content;

            if (_content is null)
            {
                return;
            }
            
            _content.Measure(Size.Infinity);
                
            AvnView?.SetFrameSize(new CGSize(_content.DesiredSize.Width, _content.DesiredSize.Height));
                
            _topLevel.Prepare();
        }
    }
    
    public void Start()
    {
        _topLevel.StartRendering();
    }

    public void Dispose()
    {
        _topLevel.StopRendering();
        _topLevel.Dispose();
        AvnView?.Dispose();
    }
}
