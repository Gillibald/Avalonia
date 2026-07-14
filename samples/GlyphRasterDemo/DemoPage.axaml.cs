using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GlyphRasterDemo
{
    public partial class DemoPage : UserControl
    {
        private readonly DispatcherTimer _timer;
        private readonly SolidColorBrush _hueBrush = new(Colors.OrangeRed);

        private readonly TextBlock _hueText;
        private readonly RotateTransform _spin;
        private readonly ScaleTransform _zoomPlain;
        private readonly ScaleTransform _zoomRotated;
        private readonly Slider _zoomSlider;
        private readonly CheckBox _animateZoom;
        private readonly TextBlock _zoomLabel;
        private readonly TextBlock _weightCycleText;

        private double _hue;
        private double _zoom = 1;
        private bool _zoomGrowing = true;
        private bool _syncingSlider;
        private int _weightStep = 100;
        private int _weight = 400;
        private int _tick;

        public DemoPage()
        {
            AvaloniaXamlLoader.Load(this);

            var scope = this.FindNameScope()!;

            _hueText = scope.Find<TextBlock>("HueText")!;
            _zoomSlider = scope.Find<Slider>("ZoomSlider")!;
            _animateZoom = scope.Find<CheckBox>("AnimateZoom")!;
            _zoomLabel = scope.Find<TextBlock>("ZoomLabel")!;
            _weightCycleText = scope.Find<TextBlock>("WeightCycleText")!;

            // Transforms cannot carry x:Name, so the animated ones are created here.
            _spin = new RotateTransform();
            _zoomPlain = new ScaleTransform();
            _zoomRotated = new ScaleTransform();
            scope.Find<TextBlock>("SpinText")!.RenderTransform = _spin;
            scope.Find<TextBlock>("ZoomPlainText")!.RenderTransform = _zoomPlain;
            scope.Find<TextBlock>("ZoomRotatedText")!.RenderTransform = new TransformGroup
            {
                Children = { _zoomRotated, new RotateTransform(20) },
            };

            _hueText.Foreground = _hueBrush;
            _zoomSlider.ValueChanged += (_, e) =>
            {
                if (!_syncingSlider && _animateZoom.IsChecked != true)
                {
                    _zoom = e.NewValue;
                    ApplyZoom();
                }
            };

            ApplyZoom();

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _tick++;

            // Foreground hue sweep — the mask stays cached, only the tint moves.
            _hue = (_hue + 0.8) % 360;
            _hueBrush.Color = HsvColor.ToRgb(_hue, 0.85, 0.9);

            // Continuous rotation — Slug redraws from the cached run artifact; the footprint
            // bucket drifts slowly with the angle, everything else is reuse.
            _spin.Angle = (_spin.Angle + 0.7) % 360;

            // Zoom ping-pong, multiplicative so the sweep feels uniform across the ladder.
            if (_animateZoom.IsChecked == true)
            {
                _zoom = _zoomGrowing ? _zoom * 1.012 : _zoom / 1.012;

                if (_zoom >= 8)
                {
                    _zoomGrowing = false;
                }
                else if (_zoom <= 0.4)
                {
                    _zoomGrowing = true;
                }

                _syncingSlider = true;
                _zoomSlider.Value = _zoom;
                _syncingSlider = false;
                ApplyZoom();
            }

            // Weight axis stepping (slower than the render tick): each step is a distinct
            // variation instance with its own managed caches.
            if (_tick % 12 == 0)
            {
                _weight += _weightStep;

                if (_weight >= 900)
                {
                    _weight = 900;
                    _weightStep = -100;
                }
                else if (_weight <= 100)
                {
                    _weight = 100;
                    _weightStep = 100;
                }

                _weightCycleText.FontWeight = (FontWeight)_weight;
                _weightCycleText.Text = $"Animated weight axis — Hamburgefonstiv wght {_weight}";
            }
        }

        private void ApplyZoom()
        {
            _zoomPlain.ScaleX = _zoomPlain.ScaleY = _zoom;
            _zoomRotated.ScaleX = _zoomRotated.ScaleY = _zoom;
            _zoomLabel.Text = FormattableString.Invariant($"scale {_zoom:0.00}x  ({26 * _zoom:0} px/em)");
        }
    }
}
