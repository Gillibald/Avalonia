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
        /// The draws made on this thread that showed a run other than a draw under a held transform
        /// shows it, while the run's transform changes from draw to draw: a bilinear stretch of an
        /// earlier raster, colour glyphs replayed as vectors, or glyphs rasterized unhinted where a
        /// static draw grid-fits them. Such a run draws as a static draw does on its first draw
        /// under an unchanged transform, so a renderer that draws only what changed must draw it
        /// once more after the motion stops (see <c>ServerCompositionTarget</c>). A running total
        /// that readers take differences of.
        /// </summary>
        public int UnsettledDraws => StretchedDraws + UnhintedMotionDraws;

        /// <summary>
        /// The draws of <see cref="UnsettledDraws"/> that stretched an earlier raster or replayed
        /// colour glyphs as vectors, rather than rasterizing the frame.
        /// </summary>
        public int StretchedDraws;

        /// <summary>The draws of <see cref="UnsettledDraws"/> that rasterized a moving run's glyphs unhinted.</summary>
        public int UnhintedMotionDraws;

        /// <summary>
        /// The <see cref="System.Diagnostics.Stopwatch"/> ticks this thread spent rasterizing glyph
        /// masks, a running total; a frame takes the difference around itself.
        /// </summary>
        public long RasterTicks;

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
