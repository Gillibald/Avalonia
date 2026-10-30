using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TextTour
{
    /// <summary>
    /// One stage of the pipeline: a control that reveals its content step by step and names the
    /// Avalonia member behind each step. Chapters draw in <see cref="Render"/>; a child (query panel,
    /// slider) sits on top of the drawing.
    /// </summary>
    internal abstract class Chapter : Decorator, IDisposable
    {
        private readonly Tween _stepProgress = new(1, TimeSpan.FromMilliseconds(320), Tween.EaseOut);
        private readonly List<Tween> _tweens = new();
        private bool _frameRequested;
        private int _step = 1;

        static Chapter()
        {
            FocusableProperty.OverrideDefaultValue<Chapter>(true);
        }

        protected Chapter(TourState state)
        {
            State = state;
            ClipToBounds = true;
            _tweens.Add(_stepProgress);
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
                AnimateTo(_stepProgress, clamped);
                OnStepChanged();
                RaiseCaptionChanged();
                InvalidateVisual();
            }
        }

        /// <summary>The step as a continuous value, eased toward <see cref="Step"/> so layers fade in.</summary>
        public double StepProgress => _stepProgress.Value;

        /// <summary>The type and member that produced what the current step shows.</summary>
        public abstract string Caption { get; }

        /// <summary>A short name of the current step for the caption bar.</summary>
        public virtual string StepName => string.Empty;

        /// <summary>Whether a detail card is open; a card can belong to a slide of its own.</summary>
        public virtual bool ShowsCard => false;

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

        /// <summary>Registers another eased value of the chapter, advanced with the step progress.</summary>
        protected Tween AddTween(double value, TimeSpan duration, Func<double, double> easing)
        {
            var tween = new Tween(value, duration, easing);
            _tweens.Add(tween);
            return tween;
        }

        /// <summary>Eases <paramref name="tween"/> to <paramref name="target"/>, or jumps there while exporting.</summary>
        protected void AnimateTo(Tween tween, double target)
        {
            tween.To(target, State.Animate);
            RequestFrame();
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            State.Changed += OnStateChanged;
            InvalidateModel();
            RequestFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            State.Changed -= OnStateChanged;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            InvalidateModel();

            // Captions can name sizes, such as the Layout chapter's wrap width.
            RaiseCaptionChanged();
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

        private void RequestFrame()
        {
            if (_frameRequested || !_tweens.Exists(t => t.IsRunning) || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            _frameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan time)
        {
            _frameRequested = false;

            foreach (var tween in _tweens)
            {
                tween.Advance(time);
            }

            InvalidateVisual();
            RequestFrame();
        }

        public virtual void Dispose()
        {
        }
    }
}
