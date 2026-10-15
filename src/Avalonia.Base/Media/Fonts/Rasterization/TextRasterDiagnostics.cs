namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The diagnostics switches of an application's managed glyph rasterization, for profiling
    /// tools (<see cref="GlyphRasterServices.Diagnostics"/>). What they record is kept per thread
    /// (<see cref="TextRasterThreadState"/>).
    /// </summary>
    internal sealed class TextRasterDiagnostics
    {
        private volatile bool _phaseTimersEnabled;

        /// <summary>
        /// Whether the phase timers of every thread record (<see cref="GlyphPhaseTimers"/>). Set
        /// before the spans to measure begin; spans already running when it changes are dropped.
        /// </summary>
        public bool PhaseTimersEnabled
        {
            get => _phaseTimersEnabled;
            set => _phaseTimersEnabled = value;
        }

        /// <summary>The mean ticks an empty probe pair records, from <see cref="Calibrate"/>; 0 before it runs.</summary>
        public double ProbeTicks { get; private set; }

        /// <summary>
        /// Measures what an empty probe pair records into <see cref="ProbeTicks"/>, timing
        /// <paramref name="pairs"/> pairs with the timers of <paramref name="state"/>, whose totals
        /// are left as they were.
        /// </summary>
        public void Calibrate(TextRasterThreadState state, int pairs = 1_000_000)
            => ProbeTicks = state.PhaseTimers.MeasureProbe(pairs);
    }
}
