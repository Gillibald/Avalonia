using System;

namespace TextTour
{
    /// <summary>
    /// A value that eases toward a target over a fixed duration, advanced by the owner once per
    /// animation frame. Without animation the value jumps to the target.
    /// </summary>
    internal sealed class Tween
    {
        private readonly TimeSpan _duration;
        private readonly Func<double, double> _easing;
        private double _from;
        private TimeSpan? _start;

        public Tween(double value, TimeSpan duration, Func<double, double> easing)
        {
            Value = value;
            Target = value;
            _duration = duration;
            _easing = easing;
        }

        public double Value { get; private set; }

        public double Target { get; private set; }

        public bool IsRunning => Value != Target;

        public static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

        public static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

        /// <summary>Starts easing from the current value; the first frame fixes the start time.</summary>
        public void To(double target, bool animate)
        {
            Target = target;
            _from = Value;
            _start = null;

            if (!animate)
            {
                Value = target;
            }
        }

        /// <summary>Moves the value for the frame at <paramref name="time"/>; returns true while running.</summary>
        public bool Advance(TimeSpan time)
        {
            if (!IsRunning)
            {
                return false;
            }

            _start ??= time;
            var t = _duration <= TimeSpan.Zero ? 1 : Math.Clamp((time - _start.Value) / _duration, 0, 1);
            Value = t >= 1 ? Target : _from + (Target - _from) * _easing(t);

            return IsRunning;
        }
    }
}
