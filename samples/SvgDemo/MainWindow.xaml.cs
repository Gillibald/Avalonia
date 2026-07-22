using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Platform.Storage;

namespace SvgDemo
{
    public class MainWindow : Window
    {
        private readonly ObservableCollection<string> _clicks = new();
        private readonly TextBlock _hoverChainText;
        private readonly SvgControl _loadedSvg;
        private readonly TextBlock _loadedFileText;
        private readonly TextBlock _loadErrorText;
        private SvgDocument? _loadedDoc;

        public MainWindow()
        {
            InitializeComponent();

            var interactiveSvg = this.FindControl<SvgControl>("InteractiveSvg")!;
            _hoverChainText = this.FindControl<TextBlock>("HoverChainText")!;
            this.FindControl<ItemsControl>("ClickLog")!.ItemsSource = _clicks;

            // Hover: query the hit chain directly on every pointer move.
            interactiveSvg.PointerMoved += OnSvgPointerMoved;
            interactiveSvg.PointerExited += (_, _) => _hoverChainText.Text = "—";

            // Clicks: the control raises element events with the chain attached.
            interactiveSvg.ElementPointerPressed += OnSvgElementPressed;

            // Load File tab: pick a .svg from disk and present it live.
            _loadedSvg = this.FindControl<SvgControl>("LoadedSvg")!;
            _loadedFileText = this.FindControl<TextBlock>("LoadedFileText")!;
            _loadErrorText = this.FindControl<TextBlock>("LoadErrorText")!;
            this.FindControl<Button>("OpenSvgButton")!.Click += OnOpenSvgClick;
        }

        private async void OnOpenSvgClick(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open SVG file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("SVG image") { Patterns = new[] { "*.svg" } }
                }
            });

            if (files.Count == 0)
                return;

            var file = files[0];
            try
            {
                await using var stream = await file.OpenReadAsync();
                var document = SvgDocument.Load(stream);

                // The control does not own code-assigned documents, so swap in the
                // new one and dispose the document the previous load created.
                var previous = _loadedDoc;
                _loadedSvg.Source = document;
                _loadedDoc = document;
                previous?.Dispose();

                _loadedFileText.Text = file.Name;
                _loadErrorText.IsVisible = false;
            }
            catch (Exception ex)
            {
                _loadErrorText.Text = $"Could not load '{file.Name}': {ex.Message}";
                _loadErrorText.IsVisible = true;
            }
        }

        private void OnSvgPointerMoved(object? sender, PointerEventArgs e)
        {
            var svg = (SvgControl)sender!;
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
