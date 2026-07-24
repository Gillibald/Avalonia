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

        // A saturated badge bouncing on the glass: with the sweep paused and
        // the dirty-rect overlay on, each hop repaints only the badge's own
        // rects while the frosted result and its cache stay untouched - the
        // element-box region is why (a subtree-bounds region would resize and
        // invalidate on every frame of the bounce). The bounce is a compositor
        // keyframe animation on the badge's element visual Offset, mirroring
        // the sweep: it runs on the render thread at vsync, and the per-frame
        // server-side dirt it produces classifies above the sample point, so
        // the cached backdrop survives at full frame rate. (An earlier
        // DispatcherTimer drive capped the FPS counter at its tick rate,
        // which measured the driver, not the pipeline.) Offset animation is
        // render-driven churn like a RenderTransform - it never touches
        // measure; animating Margin/Width instead would bubble a measure
        // invalidation into the panel, whose unconditional InvalidateVisual
        // re-records the glass itself - the upstream coarseness pinned by the
        // measure-driven compositor test.
        private readonly Border _badge = new()
        {
            Width = 26,
            Height = 18,
            Background = Brushes.OrangeRed,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 16, 0, 0)
        };

        private readonly ToggleButton _animateChild = new() { Content = "Animate child" };

        private static readonly ImmutableDropShadowEffect s_dropShadow = new(0, 6, 16, Colors.Black, 0.4);

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
                // The Border feeds its corner radius to the backdrop, so the
                // filtered region follows the rounded shape without an explicit
                // clip - which also leaves the shadow free to paint outside the
                // panel. The shadow picker below A/Bs the two shadow kinds with
                // the dirty-rect overlay: a drop shadow Effect repaints the
                // caret plus its shadow band on each blink (the localized
                // effect region), a geometry BoxShadow repaints only the caret
                // sliver, and neither touches the cached backdrop.
                CornerRadius = new CornerRadius(radius),
                Effect = s_dropShadow,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BackdropEffect = s_effects[0].Effect,
                Child = new Panel
                {
                    Children =
                    {
                        _badge,
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

            var shadowPicker = new ComboBox
            {
                ItemsSource = new List<string> { "Drop shadow (effect)", "Box shadow", "No shadow" },
                SelectedIndex = 0,
                Width = 160
            };
            shadowPicker.SelectionChanged += (_, _) =>
            {
                _panel.Effect = shadowPicker.SelectedIndex == 0 ? s_dropShadow : null;
                _panel.BoxShadow = shadowPicker.SelectedIndex == 1
                    ? BoxShadows.Parse("0 6 16 0 #66000000")
                    : default;
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

            _animateChild.IsCheckedChanged += (_, _) =>
            {
                if (_animateChild.IsChecked == true)
                    StartBounce();
                else
                    StopBounce();
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
                            shadowPicker,
                            pause,
                            _animateChild
                        }
                    },
                    new Panel
                    {
                        Children = { _source, _panel }
                    }
                }
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _blink.Start();
            // Attach runs parent-first, so the badge has no composition visual
            // yet; defer past the attach walk and the layout pass that follows.
            if (_animateChild.IsChecked == true)
                Dispatcher.UIThread.Post(StartBounce, DispatcherPriority.Loaded);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _blink.Stop();
            StopBounce();
        }

        // A linear there-and-back on the element visual's Offset: a triangle
        // wave of +-40px horizontally and +-20px vertically about the path's
        // midpoint, looping forever on the compositor. Known caveat: the
        // layout sync overwrites Offset whenever the badge re-syncs, so the
        // animation owns Offset only while layout leaves the badge alone -
        // fine for the demo, whose badge never re-measures.
        private void StartBounce()
        {
            if (ElementComposition.GetElementVisual(_badge) is not { } visual)
                return;

            var rest = new Vector3D(_badge.Bounds.Left, _badge.Bounds.Top, 0);
            var bounce = visual.Compositor.CreateVector3DKeyFrameAnimation();
            bounce.InsertKeyFrame(0f, rest);
            bounce.InsertKeyFrame(0.5f, rest + new Vector3D(80, 40, 0));
            bounce.InsertKeyFrame(1f, rest);
            bounce.Duration = TimeSpan.FromSeconds(1.8);
            bounce.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Offset", bounce);
        }

        private void StopBounce()
        {
            if (ElementComposition.GetElementVisual(_badge) is not { } visual)
                return;

            visual.StopAnimation("Offset");
            visual.Offset = new Vector3D(_badge.Bounds.Left, _badge.Bounds.Top, 0);
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
