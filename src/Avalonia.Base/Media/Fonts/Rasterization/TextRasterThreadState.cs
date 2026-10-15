using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What managed text keeps from frame to frame for one thread of one application
    /// (<see cref="GlyphRasterServices.ForCurrentThread"/>). Only that thread reads or writes it, so
    /// its members need no synchronization.
    /// </summary>
    internal sealed class TextRasterThreadState
    {
        internal TextRasterThreadState(GlyphRasterServices services)
        {
            Services = services;
            PhaseTimers = new GlyphPhaseTimers(services.Diagnostics);
        }

        /// <summary>The services this state belongs to.</summary>
        public GlyphRasterServices Services { get; }

        /// <summary>The phase timers of glyph drawing on this thread.</summary>
        public GlyphPhaseTimers PhaseTimers { get; }

        /// <summary>How this thread draws frames of runs whose transform keeps changing.</summary>
        public MotionRasterBudget Motion { get; } = new();

        /// <summary>
        /// The state of the calling thread in the current application, or <c>null</c> when it has
        /// no font manager. For code that draws outside a drawing context, such as tools and
        /// tests calling into the caches directly; a drawing context hands its own state down.
        /// </summary>
        public static TextRasterThreadState? TryGetCurrent() => GlyphRasterServices.TryGetCurrent()?.ForCurrentThread();

        /// <summary>
        /// The state <paramref name="context"/> draws text with: its own when it is a
        /// <see cref="IGlyphTargetContext"/>, else the current application's for the calling thread.
        /// </summary>
        public static TextRasterThreadState Of(IDrawingContextImpl context)
            => context is IGlyphTargetContext target
                ? target.RasterState
                : GlyphRasterServices.Current.ForCurrentThread();
    }
}
