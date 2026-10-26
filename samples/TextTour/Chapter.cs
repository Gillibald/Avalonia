using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TextTour
{
    /// <summary>
    /// One stage of the pipeline: a control that reveals its content step by step and names the
    /// Avalonia member behind each step. Chapters draw in <see cref="Render"/>; a child (query panel,
    /// sliders) sit on top of the drawing.
    /// </summary>
    internal abstract class Chapter : Decorator, IDisposable
    {
        /// <summary>The step as a continuous value, animated toward <see cref="Step"/> so layers fade in.</summary>
        public static readonly StyledProperty<double> StepProgressProperty =
            AvaloniaProperty.Register<Chapter, double>(nameof(StepProgress), 1);

        private int _step = 1;

        static Chapter()
        {
            AffectsRender<Chapter>(StepProgressProperty);
            FocusableProperty.OverrideDefaultValue<Chapter>(true);
        }

        protected Chapter(TourState state)
        {
            State = state;
            ClipToBounds = true;
        }

        /// <summary>Raised when the caption or the step counter text changed.</summary>
        public event Action? CaptionChanged;

        public TourState State { get; }

        public abstract string Title { get; }

        public abstract int StepCount { get; }

        /// <summary>The current step, 1-based.</summary>
        public int Step
        {
            get => _step;
            set
            {
                var clamped = Math.Clamp(value, 1, StepCount);

                if (_step == clamped)
                {
                    return;
                }

                _step = clamped;
                UpdateTransitions();
                SetCurrentValue(StepProgressProperty, (double)clamped);
                OnStepChanged();
                RaiseCaptionChanged();
                InvalidateVisual();
            }
        }

        public double StepProgress
        {
            get => GetValue(StepProgressProperty);
            set => SetValue(StepProgressProperty, value);
        }

        /// <summary>The type and member that produced what the current step shows.</summary>
        public abstract string Caption { get; }

        /// <summary>A short name of the current step for the caption bar.</summary>
        public virtual string StepName => string.Empty;

        protected Palette Palette => State.Palette;

        /// <summary>Content padding inside the chapter area.</summary>
        protected double Pad => 24;

        /// <summary>
        /// Opacity of content introduced at <paramref name="step"/>: 0 before it, 1 once reached,
        /// in between while the step animates in.
        /// </summary>
        protected double Reveal(int step) => Math.Clamp(StepProgress - (step - 1), 0, 1);

        /// <summary>Chapter specific keys; returns true when the key was used.</summary>
        public virtual bool HandleKey(Key key, KeyModifiers modifiers) => false;

        /// <summary>The number of frames the exporter renders for <paramref name="step"/>.</summary>
        public virtual int ExportVariantCount(int step) => 1;

        /// <summary>
        /// Puts the chapter into the state a presenter would show at this step and variant: a hovered
        /// code point, a clicked position, a toggled view. Only the exporter calls this.
        /// </summary>
        public virtual void PrepareExport(int step, int variant)
        {
        }

        /// <summary>Puts back what <see cref="PrepareExport"/> changed in the shared state.</summary>
        public virtual void EndExport()
        {
        }

        /// <summary>A file name suffix for an export variant.</summary>
        public virtual string ExportVariantName(int step, int variant) => variant == 0 ? string.Empty : $"-{variant}";

        protected virtual void OnStepChanged()
        {
        }

        /// <summary>Called after the shared state changed; the default redraws.</summary>
        protected virtual void OnStateChanged(TourChange change)
        {
            InvalidateModel();
            RaiseCaptionChanged();
        }

        /// <summary>Drops cached geometry so the next render or pointer event rebuilds it.</summary>
        protected virtual void InvalidateModel() => InvalidateVisual();

        protected void RaiseCaptionChanged() => CaptionChanged?.Invoke();

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            State.Changed += OnStateChangedCore;
            UpdateTransitions();
            InvalidateModel();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            State.Changed -= OnStateChangedCore;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            InvalidateModel();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            // Clicking the content takes focus away from the input box, so step keys work again.
            Focus();
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Palette.Background, new Rect(Bounds.Size));
        }

        private void OnStateChangedCore(TourChange change)
        {
            UpdateTransitions();
            OnStateChanged(change);
        }

        private void UpdateTransitions()
        {
            if (!State.Animate)
            {
                Transitions = null;
            }
            else if (Transitions is null)
            {
                Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = StepProgressProperty,
                        Duration = TimeSpan.FromMilliseconds(320),
                        Easing = new CubicEaseOut(),
                    },
                };
            }
        }

        public virtual void Dispose()
        {
        }
    }
}
