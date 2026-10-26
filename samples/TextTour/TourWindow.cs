using System;
using Avalonia.Controls;

namespace TextTour
{
    /// <summary>The presenter window; it owns the tour state and the shell.</summary>
    internal sealed class TourWindow : Window
    {
        private readonly TourState _state;
        private readonly TourView _view;

        public TourWindow(TourOptions options)
        {
            _state = new TourState(options.Preset)
            {
                Dark = options.Dark,
                ContentScale = options.Scale,
                Culture = options.Culture,
            };
            _view = new TourView(_state);

            Title = "Text tour";
            Width = 1440;
            Height = 900;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = _view;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _view.FocusContent();
        }

        protected override void OnClosed(EventArgs e)
        {
            _view.Dispose();
            _state.Dispose();
            base.OnClosed(e);
        }
    }
}
