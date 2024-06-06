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
        var topLevelImpl = AvaloniaLocator.Current
            .GetRequiredService<IWindowingPlatform>().CreateEmbeddableTopLevel();
        
        if (topLevelImpl.Handle is MacOSTopLevelHandle handle)
        {
            var ptr = handle.NSView;
            
            AvnView = ObjCRuntime.Runtime.GetNSObject(ptr) as NSView;
        }

        _topLevel = new EmbeddableControlRoot(topLevelImpl);
    }
    
    public NSView? AvnView { get; }

    public Control? Content
    {
        get => _content;
        set
        {
            _content = value;
            
            _topLevel.Content = _content;

            if (_content is not null)
            {
                _content.Measure(Size.Infinity);
                
                AvnView?.SetFrameSize(new CGSize(_content.DesiredSize.Width, _content.DesiredSize.Height));
                
                _topLevel.Prepare();
            }
        }
    }
    
    public void Start()
    {
        _topLevel.StartRendering();
    }

    public void Dispose()
    {
        _topLevel.Dispose();
        AvnView?.Dispose();
    }
}
