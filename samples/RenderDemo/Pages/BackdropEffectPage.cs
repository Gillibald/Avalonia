using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;

namespace RenderDemo.Pages
{
    /// <summary>
    /// A frosted panel over content that moves entirely on the render thread.
    /// <see cref="Visual.BackdropEffect"/> filters whatever the panel is drawn
    /// over rather than the panel's own content, so the interesting question is
    /// whether it keeps up: the animation below never touches the UI thread after
    /// it is started, and nothing ever marks the panel dirty. If the backdrop
    /// lagged or froze while the shapes swept past, that would show here.
    ///
    /// The moving content is a compositor-bound <see cref="DrawingRecording"/>
    /// hosted as a <see cref="CompositionRecordingVisual"/>, the same wiring the
    /// neighbouring RecordingComposition page uses.
    /// </summary>
    public class BackdropEffectPage : UserControl
    {
        private static readonly double[] s_grayscale =
        {
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0,      0,      0,      1, 0
        };

        private static readonly (string Name, IEffect? Effect)[] s_effects =
        {
            ("Blur", new ImmutableBlurEffect(12)),
            ("Stronger blur", new ImmutableBlurEffect(24)),
            ("Grayscale", new ImmutableColorMatrixEffect(s_grayscale)),
            // Horizontal-only, so the sweep smears sideways while vertical edges
            // stay put - the point of having independent radii.
            ("Directional blur", new ImmutableAnisotropicBlurEffect(28, 0, null)),
            // Effects chain by feeding one as another's input: grayscale first,
            // then blur the result.
            ("Grayscale, then blur",
                new ImmutableAnisotropicBlurEffect(10, 10,
                    new ImmutableColorMatrixEffect(s_grayscale))),
            ("None", null),
        };

        private readonly Border _panel;
        private readonly BackdropSource _source = new();

        // A caret blinking on the glass: with the sweep paused, the dirty-rect
        // overlay should show each blink repainting only this sliver - the
        // cached backdrop result spares everything underneath the panel.
        private readonly Border _caret = new()
        {
            Width = 2,
            Height = 18,
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(120, 0, 0, 0)
        };

        private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(500) };

        public BackdropEffectPage()
        {
            const double radius = 16;
            const double panelWidth = 260;
            const double panelHeight = 150;

            _panel = new Border
            {
                Width = panelWidth,
                Height = panelHeight,
                // A backdrop only shows where the control paints, so the panel
                // carries a translucent wash; a fully transparent one composites
                // to nothing at all.
                Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
                BorderBrush = new ImmutableSolidColorBrush(Colors.White, 0.7),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(radius),
                // The backdrop is confined by the control's bounds and clip, and
                // nothing knows the shape a Border paints. Without a matching clip
                // the wash is drawn rounded while the filtered area stays square
                // and fills the corners.
                Clip = new RectangleGeometry(new Rect(0, 0, panelWidth, panelHeight), radius, radius),
                BackdropEffect = s_effects[0].Effect,
                Child = new Panel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "BackdropEffect",
                            Foreground = Brushes.Black,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        _caret
                    }
                }
            };

            // Opacity rather than IsVisible: a visibility toggle schedules a
            // layout pass, and the point of the caret is to show a pure render
            // change repainting only its own sliver.
            _blink.Tick += (_, _) => _caret.Opacity = _caret.Opacity > 0 ? 0 : 1;

            // The shadow lives on a wrapper rather than the panel: the panel's
            // rounded clip would crop an Effect-based shadow away, while an
            // Effect on a wrapper would open a filter layer the backdrop then
            // samples instead of the scene. A geometry box shadow does neither -
            // no clip on this element, no layer around the glass.
            var shadow = new Border
            {
                CornerRadius = new CornerRadius(radius),
                BoxShadow = BoxShadows.Parse("0 6 16 0 #66000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _panel
            };

            var picker = new ComboBox
            {
                ItemsSource = new List<string>(Array.ConvertAll(s_effects, e => e.Name)),
                SelectedIndex = 0,
                Width = 180
            };
            picker.SelectionChanged += (_, _) =>
            {
                var index = picker.SelectedIndex;
                if (index >= 0 && index < s_effects.Length)
                    _panel.BackdropEffect = s_effects[index].Effect;
            };

            // With the sweep paused nothing beneath the panel changes, which is
            // the case the cached backdrop exists for.
            var pause = new ToggleButton { Content = "Pause sweep" };
            pause.IsCheckedChanged += (_, _) =>
            {
                if (pause.IsChecked == true)
                    _source.Pause();
                else
                    _source.Resume();
            };

            Content = new DockPanel
            {
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Top,
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(8),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Backdrop:",
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            picker,
                            pause
                        }
                    },
                    new Panel
                    {
                        Children = { _source, shadow }
                    }
                }
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _blink.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _blink.Stop();
        }

        /// <summary>
        /// The content the panel is drawn over: a recorded drawing swept back and
        /// forth by a render-thread animation.
        /// </summary>
        private sealed class BackdropSource : Control
        {
            private const double Span = 260;

            private DrawingRecording? _recording;
            private CompositionRecordingVisual? _visual;

            public BackdropSource()
            {
                ClipToBounds = true;
            }

            protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
            {
                base.OnAttachedToVisualTree(e);

                var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
                if (compositor is null || _visual?.Compositor == compositor)
                    return;

                // Saturated, high-contrast shapes: a blur backdrop is only legible
                // against colour that actually varies underneath it.
                _recording = DrawingRecording.Create(compositor, ctx =>
                {
                    var colors = new[]
                    {
                        Colors.Crimson, Colors.DodgerBlue, Colors.Gold,
                        Colors.MediumSeaGreen, Colors.MediumVioletRed
                    };

                    for (var i = 0; i < colors.Length; i++)
                    {
                        var x = i * 110;
                        ctx.DrawRectangle(
                            new ImmutableSolidColorBrush(colors[i]), null,
                            new Rect(x, 40, 90, 300));
                        ctx.DrawEllipse(
                            new ImmutableSolidColorBrush(colors[(i + 2) % colors.Length]), null,
                            new Rect(x + 20, 150, 100, 100));
                    }

                    ctx.DrawLine(
                        new ImmutablePen(Brushes.Black, 8),
                        new Point(0, 320), new Point(600, 320));
                });

                _visual = compositor.CreateRecordingVisual();
                _visual.Recording = _recording;
                _visual.Size = new Vector(600, 400);
                ElementComposition.SetElementChildVisual(this, _visual);

                StartSweep(_visual);
            }

            public void Pause() => _visual?.StopAnimation("Offset");

            public void Resume()
            {
                if (_visual is { } visual)
                    StartSweep(visual);
            }

            // Sent once; the sweep runs on the compositor from here on, so the
            // backdrop has to track a surface changing with no UI-thread work.
            private static void StartSweep(CompositionRecordingVisual visual)
            {
                var sweep = visual.Compositor.CreateVector3DKeyFrameAnimation();
                sweep.InsertKeyFrame(0f, new Vector3D(-Span, 0, 0));
                sweep.InsertKeyFrame(0.5f, new Vector3D(Span, 0, 0));
                sweep.InsertKeyFrame(1f, new Vector3D(-Span, 0, 0));
                sweep.Duration = TimeSpan.FromSeconds(6);
                sweep.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation("Offset", sweep);
            }

            protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
            {
                base.OnDetachedFromVisualTree(e);

                ElementComposition.SetElementChildVisual(this, null);
                _visual = null;
                _recording?.Dispose();
                _recording = null;
            }
        }
    }
}
