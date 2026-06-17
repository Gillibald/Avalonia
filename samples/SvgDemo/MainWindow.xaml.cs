using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Svg;

namespace SvgDemo
{
    public class MainWindow : Window
    {
        private readonly ObservableCollection<string> _clicks = new();
        private readonly TextBlock _hoverChainText;

        public MainWindow()
        {
            InitializeComponent();

            var interactiveSvg = this.FindControl<Avalonia.Svg.SvgControl>("InteractiveSvg")!;
            _hoverChainText = this.FindControl<TextBlock>("HoverChainText")!;
            this.FindControl<ItemsControl>("ClickLog")!.ItemsSource = _clicks;

            // Hover: query the hit chain directly on every pointer move.
            interactiveSvg.PointerMoved += OnSvgPointerMoved;
            interactiveSvg.PointerExited += (_, _) => _hoverChainText.Text = "—";

            // Clicks: the control raises element events with the chain attached.
            interactiveSvg.ElementPointerPressed += OnSvgElementPressed;
        }

        private void OnSvgPointerMoved(object? sender, PointerEventArgs e)
        {
            var svg = (Avalonia.Svg.SvgControl)sender!;
            var chain = svg.HitTestElements(e.GetPosition(svg));
            _hoverChainText.Text = chain.Count > 0 ? FormatChain(chain) : "—";
        }

        private void OnSvgElementPressed(object? sender, SvgElementPointerEventArgs e)
        {
            _clicks.Insert(0, $"{FormatElement(e.Element),-22} via {FormatChain(e.Elements)}");
            while (_clicks.Count > 8)
                _clicks.RemoveAt(_clicks.Count - 1);
        }

        private static string FormatChain(System.Collections.Generic.IReadOnlyList<SvgElement> chain)
            => string.Join(" → ", chain.Select(FormatElement));

        private static string FormatElement(SvgElement element)
            => element.Id is { Length: > 0 } id ? $"{element.Name}#{id}" : element.Name;

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
