using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Native;


namespace Avalonia.Embedding.Mac;

public class AvaloniaView : NSView
{
    private readonly EmbeddableControlRoot _topLevel;
    private Control? _content;

    public AvaloniaView()
    {
        AutoresizesSubviews = true;
        
        var topLevelImpl = AvaloniaLocator.Current.GetRequiredService<AvaloniaNativePlatform>().CreateTopLevelImpl();
        
        if (topLevelImpl.Native is not null)
        {
            var ptr = topLevelImpl.Native.ObtainNSViewHandle();
            
            AvnView = ObjCRuntime.Runtime.GetNSObject(ptr) as NSView;
        }

        _topLevel = new EmbeddableControlRoot(topLevelImpl);

        _topLevel.Content = new TextBox { Text = "Hello World" };
    }
    
    public NSView? AvnView { get; }

    public Control? Content
    {
        get => _content;
        set
        {
            if (_content != null)
            {
                _content.PropertyChanged -= ContentOnPropertyChanged;
            }
            
            _content = value;
            _topLevel.Content = _content;

            if (value != null)
            {
                value.PropertyChanged += ContentOnPropertyChanged;
            }
        }
    }

    private void ContentOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Visual.BoundsProperty))
        {
            ContentSizeChanged();
        }
    }

    private void ContentSizeChanged()
    {
        AvnView?.SetFrameSize(_content != null ?
            new CGSize(_content.DesiredSize.Width, _content.DesiredSize.Height) :
            new CGSize());
    }

    public void Start()
    {
        _topLevel.Prepare();
        _topLevel.StartRendering();
    }
}
