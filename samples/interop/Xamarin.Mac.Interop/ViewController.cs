using Avalonia.Controls;
using ObjCRuntime;

namespace Xamarin.Mac.Interop
{
    public partial class ViewController : NSViewController
    {
        protected ViewController(NativeHandle handle) : base(handle)
        {
            // This constructor is required if the view controller is loaded from a xib or a storyboard.
            // Do not put any initialization here, use ViewDidLoad instead.
        }

        private AvaloniaView _embeddedView = new AvaloniaView();

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            // Do any additional setup after loading the view.
            if (_embeddedView.AvnView != null)
            {
                View.AddSubview(_embeddedView.AvnView);
                
                _embeddedView.Start();

                var panel = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Avalonia TextBlock" },
                        new TextBox { Text = "Avalonia TextBox" }
                    },
                    Width = 200,
                    Height = 100
                };

                _embeddedView.Content = panel;
            }
        }

        public override NSObject RepresentedObject
        {
            get => base.RepresentedObject;
            set
            {
                base.RepresentedObject = value;

                // Update the view, if already loaded.
            }
        }
    }
}
